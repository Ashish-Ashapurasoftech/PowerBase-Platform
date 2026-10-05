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
        => DeleteRevisions(value, [path]);

    public static string RestoreRevision(object? value, string path, string? restoredBy = null,
        DateTime? restoredOn = null, int revisionLimit = 3)
    {
        var root = Parse(value) ?? throw new InvalidOperationException("No file attachment was found.");
        var attachment = FirstAttachment(root) ?? throw new InvalidOperationException("No file attachment was found.");
        if (string.Equals(attachment["revisionId"]?.ToString(), path, StringComparison.Ordinal) ||
            (attachment["revisionId"] == null &&
             string.Equals(attachment["path"]?.ToString(), path, StringComparison.Ordinal)))
            throw new InvalidOperationException("The selected revision is already current.");
        if (attachment["revisions"] is not JsonArray revisions)
            throw new InvalidOperationException("The selected revision no longer exists. Reload the record.");

        var historical = revisions.OfType<JsonObject>().ToArray();
        var selected = ResolveRevisions(historical, [path]).Single();

        EnsureRevisionMetadata(attachment, revisions);
        var operationTime = restoredOn ?? DateTime.UtcNow;

        // Restoration is append-only: retain the selected historical revision and the former
        // current revision, then create a new logical revision that safely reuses the file blob.
        var priorCurrent = RevisionOnlyClone(attachment);
        priorCurrent["replacedOn"] = operationTime;
        revisions.Add(priorCurrent);

        // The configured limit includes the new current revision. Keep only the newest
        // historical entries while preserving the monotonic sequence on the current value.
        var historicalLimit = Math.Clamp(revisionLimit, 1, 100) - 1;
        while (revisions.Count > historicalLimit) revisions.RemoveAt(0);

        var restored = RevisionOnlyClone(selected);
        foreach (var key in new[] { "revisionId", "revisionNumber", "revisionSequence", "uploadedOn",
                     "uploadedBy", "replacedOn", "restoredOn", "restoredBy" })
            restored.Remove(key);
        var nextRevision = RevisionSequence(attachment, revisions.OfType<JsonObject>()) + 1;
        restored["revisionId"] = Guid.NewGuid().ToString("N");
        restored["revisionNumber"] = nextRevision;
        restored["revisionSequence"] = nextRevision;
        restored["uploadedOn"] = operationTime;
        restored["restoredOn"] = operationTime;
        if (!string.IsNullOrWhiteSpace(restoredBy))
        {
            restored["uploadedBy"] = restoredBy.Trim();
            restored["restoredBy"] = restoredBy.Trim();
        }
        restored["revisions"] = revisions.DeepClone();
        if (attachment["reservation"] != null)
            restored["reservation"] = attachment["reservation"]!.DeepClone();
        if (root is JsonArray array)
        {
            array[0] = restored;
            return array.ToJsonString();
        }
        return restored.ToJsonString();
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
        var selected = ResolveRevisions(versions, paths);
        EnsureRevisionMetadata(attachment, attachment["revisions"] as JsonArray ?? new JsonArray());
        var remaining = versions.Where(version => !selected.Contains(version)).ToList();
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
        current["revisionSequence"] = RevisionSequence(attachment, versions);
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
        DateTime? uploadedOn = null,
        int revisionLimit = 3)
    {
        if (newValue == null) return null;
        var previous = FirstAttachment(oldValue);
        var root = Parse(newValue);
        var current = FirstAttachment(root);
        if (current == null) return newValue;
        if (previous == null)
        {
            current["revisionId"] ??= Guid.NewGuid().ToString("N");
            current["revisionNumber"] ??= 1;
            current["revisionSequence"] ??= 1;
            current["uploadedOn"] ??= JsonValue.Create(uploadedOn ?? DateTime.UtcNow);
            if (current["uploadedBy"] == null && !string.IsNullOrWhiteSpace(uploadedBy))
                current["uploadedBy"] = uploadedBy.Trim();
            return root!.ToJsonString();
        }

        var oldPath = previous["path"]?.ToString();
        var newPath = current["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return newValue;

        var revisions = previous["revisions"] is JsonArray saved
            ? (JsonArray)saved.DeepClone() : new JsonArray();
        EnsureRevisionMetadata(previous, revisions);
        var sequence = RevisionSequence(previous, revisions.OfType<JsonObject>());
        if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            var prior = RevisionOnlyClone(previous);
            prior["replacedOn"] = DateTime.UtcNow;
            revisions.Add(prior);
            // RevisionLimit includes the current file, matching Quickbase field settings.
            var previousLimit = Math.Clamp(revisionLimit, 1, 100) - 1;
            while (revisions.Count > previousLimit) revisions.RemoveAt(0);
            current["revisionId"] = Guid.NewGuid().ToString("N");
            current["revisionNumber"] = sequence + 1;
            current["revisionSequence"] = sequence + 1;
            current["uploadedOn"] ??= JsonValue.Create(uploadedOn ?? DateTime.UtcNow);
            if (current["uploadedBy"] == null && !string.IsNullOrWhiteSpace(uploadedBy))
                current["uploadedBy"] = uploadedBy.Trim();
        }
        else if (previous["uploadedOn"] != null)
        {
            current["uploadedOn"] = previous["uploadedOn"]!.DeepClone();
            foreach (var key in new[] { "revisionId", "revisionNumber", "revisionSequence" })
                if (previous[key] != null) current[key] = previous[key]!.DeepClone();
        }

        if (revisions.Count > 0) current["revisions"] = revisions;
        else current.Remove("revisions");

        var reservation = Read(oldValue);
        if (reservation != null && Read(newValue) == null)
            current["reservation"] = previous["reservation"]!.DeepClone();
        return root!.ToJsonString();
    }

    public static IReadOnlySet<string> ReferencedPaths(object? value)
    {
        var attachment = FirstAttachment(value);
        if (attachment == null) return new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        AddPath(attachment, paths);
        if (attachment["revisions"] is JsonArray revisions)
            foreach (var revision in revisions.OfType<JsonObject>()) AddPath(revision, paths);
        return paths;
    }

    private static HashSet<JsonObject> ResolveRevisions(IReadOnlyList<JsonObject> versions,
        IReadOnlyCollection<string> identifiers)
    {
        var selected = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var identifier in identifiers)
        {
            var matches = versions.Where(version =>
                string.Equals(version["revisionId"]?.ToString(), identifier, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0)
                matches = versions.Where(version =>
                    string.Equals(version["path"]?.ToString(), identifier, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("A selected revision no longer exists. Reload the record.");
            selected.Add(matches[0]);
        }
        return selected;
    }

    private static void EnsureRevisionMetadata(JsonObject current, JsonArray revisions)
    {
        var ordered = revisions.OfType<JsonObject>().Append(current).ToArray();
        var next = 0;
        foreach (var revision in ordered)
        {
            revision["revisionId"] ??= Guid.NewGuid().ToString("N");
            if (int.TryParse(revision["revisionNumber"]?.ToString(), out var existing) && existing > 0)
                next = Math.Max(next, existing);
            else
                revision["revisionNumber"] = ++next;
        }
        current["revisionSequence"] = Math.Max(RevisionSequence(current, ordered), next);
    }

    private static int RevisionSequence(JsonObject current, IEnumerable<JsonObject> versions)
    {
        var sequence = int.TryParse(current["revisionSequence"]?.ToString(), out var stored) ? stored : 0;
        foreach (var version in versions)
            if (int.TryParse(version["revisionNumber"]?.ToString(), out var number))
                sequence = Math.Max(sequence, number);
        return sequence;
    }

    private static JsonObject RevisionOnlyClone(JsonObject source)
    {
        var clone = (JsonObject)source.DeepClone();
        clone.Remove("revisions");
        clone.Remove("reservation");
        clone.Remove("revisionSequence");
        return clone;
    }

    private static void AddPath(JsonObject attachment, ISet<string> paths)
    {
        var path = attachment["path"]?.ToString();
        if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
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
