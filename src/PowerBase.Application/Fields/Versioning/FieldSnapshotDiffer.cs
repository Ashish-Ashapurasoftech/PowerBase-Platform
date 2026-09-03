using System.Text.Json;

namespace PowerBase.Application.Fields.Versioning;

/// <summary>Produces the structured, per-property diff between two field-settings snapshots — the
/// single source of truth for "what changed" used by both the Update path (diffing the field's
/// prior state against the incoming request) and the Restore path (diffing the current state
/// against the version being restored). Centralizing this here is what keeps those two flows from
/// duplicating comparison logic (see FieldVersionService, which both call into).</summary>
public static class FieldSnapshotDiffer
{
    public static IReadOnlyList<FieldChangeEntry> Diff(FieldSnapshot before, FieldSnapshot after)
    {
        var changes = new List<FieldChangeEntry>();

        void Compare<T>(string propertyName, T oldValue, T newValue)
        {
            if (Equals(oldValue, newValue)) return;
            changes.Add(new FieldChangeEntry(propertyName, oldValue?.ToString(), newValue?.ToString()));
        }

        Compare(nameof(FieldSnapshot.Label), before.Label, after.Label);
        Compare(nameof(FieldSnapshot.Description), before.Description, after.Description);
        Compare(nameof(FieldSnapshot.IsRequired), before.IsRequired, after.IsRequired);
        Compare(nameof(FieldSnapshot.DefaultValue), before.DefaultValue, after.DefaultValue);
        Compare(nameof(FieldSnapshot.IsSearchable), before.IsSearchable, after.IsSearchable);
        Compare(nameof(FieldSnapshot.IsSortable), before.IsSortable, after.IsSortable);
        Compare(nameof(FieldSnapshot.IsFilterable), before.IsFilterable, after.IsFilterable);
        Compare(nameof(FieldSnapshot.IsReportable), before.IsReportable, after.IsReportable);
        Compare(nameof(FieldSnapshot.IsAuditable), before.IsAuditable, after.IsAuditable);
        Compare(nameof(FieldSnapshot.IsUnique), before.IsUnique, after.IsUnique);
        Compare(nameof(FieldSnapshot.IsEncrypted), before.IsEncrypted, after.IsEncrypted);

        changes.AddRange(DiffSettings(before.Settings, after.Settings));

        return changes;
    }

    /// <summary>Diffs the Settings JSON blob key-by-key (e.g. "Settings.MaxLength": 50 → 100)
    /// rather than as one opaque blob, so the Audit History detail view stays readable. Falls back
    /// to a single whole-value "Settings" entry if either side isn't a JSON object — malformed
    /// Settings is rejected earlier by FieldSettingsValidatorRegistry, so this is only reachable for
    /// field types with no Settings shape at all (plain null on both/either side).</summary>
    private static IEnumerable<FieldChangeEntry> DiffSettings(string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) yield break;

        var beforeProps = TryGetObjectProperties(before);
        var afterProps = TryGetObjectProperties(after);

        if (beforeProps is null || afterProps is null)
        {
            yield return new FieldChangeEntry("Settings", before, after);
            yield break;
        }

        foreach (var key in beforeProps.Keys.Union(afterProps.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var hasOld = beforeProps.TryGetValue(key, out var oldRaw);
            var hasNew = afterProps.TryGetValue(key, out var newRaw);
            if (hasOld && hasNew && string.Equals(oldRaw, newRaw, StringComparison.Ordinal)) continue;

            yield return new FieldChangeEntry($"Settings.{key}", hasOld ? oldRaw : null, hasNew ? newRaw : null);
        }
    }

    private static Dictionary<string, string>? TryGetObjectProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
