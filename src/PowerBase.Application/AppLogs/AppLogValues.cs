using System.Text.Json;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.AppLogs;

/// <summary>
/// Record values for the application log, as { "Field label": "value" } JSON — the same shape audit.ActivityLog's
/// OldValues / NewValues use. Every field counts here: the "auditable" flag only filters the record page Stream
/// (audit.ActivityLog), never this log. Only system fields and computed fields (formulas, lookups, summaries) are skipped.
/// </summary>
public static class AppLogValues
{
    public static bool IsLogged(AppField field) =>
        field.Fid.HasValue && !field.IsSystem && !PhysicalNaming.IsComputedTypeCode(field.TypeCode);

    /// <summary>A record's values (added with / had when deleted): the logged fields that hold a value.</summary>
    public static string? Snapshot(IEnumerable<AppField> fields, Func<AppField, object?> valueOf)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(IsLogged))
        {
            var text = Text(valueOf(field));
            if (!string.IsNullOrEmpty(text)) values[field.Label ?? field.Name] = text;
        }
        return values.Count == 0 ? null : JsonSerializer.Serialize(values);
    }

    /// <summary>Old and new values of the fields an update changed.</summary>
    public static (string? Old, string? New) Changes(IEnumerable<AppField> changedFields, Func<AppField, object?> oldOf, Func<AppField, object?> newOf)
    {
        var oldValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var newValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in changedFields.Where(IsLogged))
        {
            var label = field.Label ?? field.Name;
            oldValues[label] = Text(oldOf(field));
            newValues[label] = Text(newOf(field));
        }
        return oldValues.Count == 0 ? (null, null) : (JsonSerializer.Serialize(oldValues), JsonSerializer.Serialize(newValues));
    }

    public static string? Text(object? value) => value switch
    {
        null or DBNull => null,
        DateTime d => d.ToString("o"),
        DateTimeOffset d => d.ToString("o"),
        _ => value.ToString(),
    };
}
