using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>The human-readable name of a record for run history: the table's display field, Name/Title, the first text value, then the first plain value.
/// Values are keyed by <c>fid_N</c>. Falls back to the record's public id only when the record holds nothing readable.</summary>
public static class PipelineRecordDisplayName
{
    public static string Resolve(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<string, object?> fieldValues, string recordPublicId)
    {
        if (table.DisplayFieldId.HasValue)
        {
            var displayField = fields.FirstOrDefault(f => f.Id == table.DisplayFieldId.Value);
            if (displayField != null && displayField.Fid.HasValue)
            {
                var fidKey = $"fid_{displayField.Fid.Value}";
                if (fieldValues.TryGetValue(fidKey, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
                {
                    return val.ToString()!;
                }
            }
        }
        
        var fallbackField = fields.FirstOrDefault(f => 
            f.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) || 
            f.Name.Equals("Title", StringComparison.OrdinalIgnoreCase) || 
            (f.Label != null && (f.Label.Equals("Name", StringComparison.OrdinalIgnoreCase) || f.Label.Equals("Title", StringComparison.OrdinalIgnoreCase))));
            
        if (fallbackField != null && fallbackField.Fid.HasValue)
        {
            var fidKey = $"fid_{fallbackField.Fid.Value}";
            if (fieldValues.TryGetValue(fidKey, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
            {
                return val.ToString()!;
            }
        }
        
        foreach (var field in fields)
        {
            if (field.Fid.HasValue && field.TypeCode.ToUpperInvariant() == "TEXT")
            {
                var fidKey = $"fid_{field.Fid.Value}";
                if (fieldValues.TryGetValue(fidKey, out var val) && val != null && !string.IsNullOrWhiteSpace(val.ToString()))
                {
                    return val.ToString()!;
                }
            }
        }

        // No display field / Name / Title / Text value: use the first plain stored value (auto-number, number, date, choice…)
        // so history reads "Order 1042" rather than a GUID. Computed, boolean and structured values are not a name.
        foreach (var field in fields.Where(f => f.Fid.HasValue && !f.IsDeleted && !PowerBase.Domain.Constants.PhysicalNaming.IsComputedTypeCode(f.TypeCode)
                     && !string.Equals(f.TypeCode, "Boolean", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(f.TypeCode, "Reference", StringComparison.OrdinalIgnoreCase)))
        {
            if (!fieldValues.TryGetValue($"fid_{field.Fid!.Value}", out var val) || val == null) continue;
            if (val is System.Collections.IEnumerable and not string) continue;
            var text = val.ToString();
            if (!string.IsNullOrWhiteSpace(text) && !Guid.TryParse(text, out _)) return text!;
        }

        return recordPublicId;
    }
}
