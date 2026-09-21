using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace PowerBase.Application.Records;

public sealed record FileReservation(long UserId, string UserName, string? Comment, DateTime ReservedOn);

public static class FileReservationContract
{
    public static FileReservation? Read(object? value)
    {
        var attachment = FirstAttachment(value);
        if (attachment?["reservation"] is not JsonObject reservation ||
            !long.TryParse(reservation["userId"]?.ToString(), out var userId)) return null;
        var userName = reservation["userName"]?.GetValue<string>() ?? string.Empty;
        var comment = reservation["comment"]?.GetValue<string>();
        return DateTime.TryParse(reservation["reservedOn"]?.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var reservedOn)
            ? new FileReservation(userId, userName, comment, reservedOn)
            : null;
    }

    public static string Reserve(object? value, FileReservation reservation)
    {
        var root = Parse(value) ?? throw new InvalidOperationException("A file must be attached before it can be reserved.");
        var attachment = FirstAttachment(root) ?? throw new InvalidOperationException("A file must be attached before it can be reserved.");
        attachment["reservation"] = JsonSerializer.SerializeToNode(new
        {
            userId = reservation.UserId,
            userName = reservation.UserName,
            comment = reservation.Comment,
            reservedOn = reservation.ReservedOn
        });
        return root.ToJsonString();
    }

    public static string Release(object? value)
    {
        var root = Parse(value) ?? throw new InvalidOperationException("No file attachment was found.");
        var attachment = FirstAttachment(root) ?? throw new InvalidOperationException("No file attachment was found.");
        attachment.Remove("reservation");
        return root.ToJsonString();
    }

    public static string? DeleteRevision(object? value, string path)
    {
        var root = Parse(value) ?? throw new InvalidOperationException("No file attachment was found.");
        var attachment = FirstAttachment(root) ?? throw new InvalidOperationException("No file attachment was found.");
        if (string.Equals(attachment["path"]?.ToString(), path, StringComparison.Ordinal))
        {
            if (attachment["revisions"] is not JsonArray revisions || revisions.Count == 0)
                return null;
            var previous = (JsonObject)revisions[revisions.Count - 1]!.DeepClone();
            revisions.RemoveAt(revisions.Count - 1);
            if (revisions.Count > 0) previous["revisions"] = revisions.DeepClone();
            if (attachment["reservation"] != null)
                previous["reservation"] = attachment["reservation"]!.DeepClone();
            if (root is JsonArray array)
            {
                array[0] = previous;
                return array.ToJsonString();
            }
            return previous.ToJsonString();
        }
        if (attachment["revisions"] is JsonArray history)
        {
            var matches = Enumerable.Range(0, history.Count)
                .Where(i => string.Equals(history[i]?["path"]?.ToString(), path, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 1)
            {
                history.RemoveAt(matches[0]);
                if (history.Count == 0) attachment.Remove("revisions");
                return root.ToJsonString();
            }
        }
        throw new InvalidOperationException("The selected revision no longer exists. Reload the record.");
    }

    public static string? DeleteRevisions(object? value, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0 || paths.Any(string.IsNullOrWhiteSpace) ||
            paths.Count != paths.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("Select valid revisions to delete.");
        var root = Parse(value) ?? throw new InvalidOperationException("No file attachment was found.");
        var attachment = FirstAttachment(root) ?? throw new InvalidOperationException("No file attachment was found.");
        var versions = new List<JsonObject>();
        if (attachment["revisions"] is JsonArray revisions)
            versions.AddRange(revisions.OfType<JsonObject>());
        versions.Add(attachment);
        var selected = paths.ToHashSet(StringComparer.Ordinal);
        if (versions.Count(version => selected.Contains(version["path"]?.ToString() ?? string.Empty)) != selected.Count)
            throw new InvalidOperationException("A selected revision no longer exists. Reload the record.");
        var remaining = versions.Where(version => !selected.Contains(version["path"]?.ToString() ?? string.Empty)).ToList();
        if (remaining.Count == 0)
        {
            if (root is JsonArray array)
            {
                array.RemoveAt(0);
                return array.Count == 0 ? null : array.ToJsonString();
            }
            return null;
        }
        var current = (JsonObject)remaining[^1].DeepClone();
        current.Remove("revisions");
        current.Remove("reservation");
        if (remaining.Count > 1)
        {
            var history = new JsonArray();
            foreach (var previous in remaining.Take(remaining.Count - 1))
                history.Add(previous.DeepClone());
            current["revisions"] = history;
        }
        if (attachment["reservation"] != null)
            current["reservation"] = attachment["reservation"]!.DeepClone();
        if (root is JsonArray attachments)
        {
            attachments[0] = current;
            return attachments.ToJsonString();
        }
        return current.ToJsonString();
    }

    public static object? PreserveReservation(
        object? oldValue,
        object? newValue,
        string? uploadedBy = null,
        DateTime? uploadedOn = null)
    {
        if (newValue == null) return null;
        var previous = FirstAttachment(oldValue);
        var root = Parse(newValue);
        var current = FirstAttachment(root);
        if (previous == null || current == null) return newValue;

        var oldPath = previous["path"]?.ToString();
        var newPath = current["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return newValue;

        var revisions = previous["revisions"] is JsonArray saved
            ? (JsonArray)saved.DeepClone() : new JsonArray();
        if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            var prior = new JsonObject();
            foreach (var key in new[] { "name", "path", "size", "type", "uploadedOn", "uploadedBy" })
                if (previous[key] != null) prior[key] = previous[key]!.DeepClone();
            prior["replacedOn"] = DateTime.UtcNow;
            revisions.Add(prior);
            // Quickbase keeps the current file and two older versions by default.
            while (revisions.Count > 2) revisions.RemoveAt(0);
            current["uploadedOn"] ??= JsonValue.Create(uploadedOn ?? DateTime.UtcNow);
            if (current["uploadedBy"] == null && !string.IsNullOrWhiteSpace(uploadedBy))
                current["uploadedBy"] = uploadedBy.Trim();
        }
        else if (previous["uploadedOn"] != null)
        {
            current["uploadedOn"] = previous["uploadedOn"]!.DeepClone();
        }

        if (revisions.Count > 0) current["revisions"] = revisions;
        else current.Remove("revisions");

        var reservation = Read(oldValue);
        if (reservation != null && Read(newValue) == null)
            current["reservation"] = previous["reservation"]!.DeepClone();
        return root!.ToJsonString();
    }

    private static JsonNode? Parse(object? value)
    {
        if (value == null) return null;
        if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static JsonObject? FirstAttachment(object? value) => FirstAttachment(Parse(value));

    private static JsonObject? FirstAttachment(JsonNode? root)
    {
        if (root is JsonObject attachment) return attachment;
        if (root is JsonArray array) return array.OfType<JsonObject>().FirstOrDefault();
        return null;
    }
}
