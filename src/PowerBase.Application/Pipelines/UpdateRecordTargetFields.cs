using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Which fields an Update Record step may set, as Quickbase allows: only the fields the step it acts on outputs —
/// a Look Up's Fields, a Search's (or On New Bulk Event's, through the Loop) and an On New Event trigger's Fields for
/// subsequent steps, and the fields chosen on a Create Record step; on another Update Record step, what that step's target outputs. Other targets have no such list and are not limited.
/// </summary>
public static class UpdateRecordTargetFields
{
    /// <summary>The field ids (as "N") the target outputs, or null when the target has no field list to hold the step to.</summary>
    public static HashSet<string>? Allowed(SavePipelineStepDto target, IReadOnlyDictionary<string, SavePipelineStepDto> stepById,
        SavePipelineStepDto? trigger = null, int depth = 0)
    {
        // An Update Record acting on another Update Record step can set what that step's own target outputs (Quickbase follows the chain).
        if (target.Subtype == "update-record")
        {
            if (depth > 20 || PipelineRecordSource.IsRecordId(target.ConfigJson)) return null;
            var next = TargetOf(Read(target.ConfigJson, "targetRecordId"), stepById, trigger);
            return next is null ? null : Allowed(next, stepById, trigger, depth + 1);
        }

        var source = target;
        if (target.Type == "loop" || target.Subtype == "for-each")
        {
            var loopOver = Read(target.ConfigJson, "loopOverStepId");
            if (string.IsNullOrEmpty(loopOver) || !stepById.TryGetValue(loopOver, out var listStep)) return null;
            source = listStep;
        }

        return source.Subtype switch
        {
            "look-up-record" or "search-records" or "new-event" or "new-bulk-event" => Ids(source.ConfigJson, "subsequentFields"),
            // A Create Record step outputs every field chosen on it, a field left without a value too (kept in outputFields).
            "create-record" => Union(Ids(source.ConfigJson, "fieldMappings"), Ids(source.ConfigJson, "outputFields")),
            _ => null
        };
    }

    /// <summary>The step a target reference names: "{{steps.ref_a.RecordPublicId}}", "{{trigger.RecordPublicId}}" or a ref id.</summary>
    public static SavePipelineStepDto? TargetOf(string? reference, IReadOnlyDictionary<string, SavePipelineStepDto> stepById, SavePipelineStepDto? trigger)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var refId = reference.Trim();
        var match = Regex.Match(refId, @"^\{\{\s*(?:steps\.)?([A-Za-z0-9_]+)");
        if (match.Success) refId = match.Groups[1].Value;
        if (refId == "trigger") return trigger;
        return stepById.TryGetValue(refId, out var step) ? step : null;
    }

    private static HashSet<string>? Union(HashSet<string>? a, HashSet<string>? b)
    {
        if (a is null && b is null) return null;
        var set = new HashSet<string>(a ?? [], StringComparer.OrdinalIgnoreCase);
        if (b is not null) set.UnionWith(b);
        return set;
    }

    /// <summary>The fields the Update Record step sets that are not in <paramref name="allowed"/>, as "N".</summary>
    public static IReadOnlyList<string> NotOutput(string? updateConfigJson, HashSet<string> allowed) =>
        (Ids(updateConfigJson, "fieldMappings") ?? []).Where(id => !allowed.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();

    public static string Message(string stepRef, string targetRef, IReadOnlyList<string> fields) =>
        $"Update Record step '{stepRef}' sets {(fields.Count == 1 ? "field" : "fields")} {string.Join(", ", fields.Select(f => $"'fid_{f}'"))} " +
        $"that step '{targetRef}' does not output. As in Quickbase, an Update Record step can only set the fields the step it acts on outputs: " +
        "add them to that step's fields first.";

    private static string? Read(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static HashSet<string>? Ids(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!doc.RootElement.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return set;
            foreach (var item in list.EnumerateArray())
            {
                var text = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Number => item.GetRawText(),
                    JsonValueKind.Object when item.TryGetProperty("field", out var f) => f.ValueKind == JsonValueKind.Number ? f.GetRawText() : f.GetString(),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(text)) continue;
                // "fid_12", "12", or an Address part "fid_12.city" / "fid_12::city": the field is 12.
                var match = Regex.Match(text.Trim(), @"^(?:fid_)?(\d+)", RegexOptions.IgnoreCase);
                set.Add(match.Success ? match.Groups[1].Value : text.Trim());
            }
            return set;
        }
        catch (JsonException) { return null; }
    }
}
