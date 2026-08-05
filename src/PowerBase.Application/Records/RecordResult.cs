using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Records;

public class RecordResult
{
    public Guid Id { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? ModifiedOn { get; init; }
    /// <summary>Internal userId (long) of the user who created this record. Used by the UI for OwnRecords scope gating.</summary>
    public long CreatedBy { get; init; }
    public Dictionary<string, object?> Fields { get; init; } = new();

    public static RecordResult FromRow(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, string>? userNames = null,
        IReadOnlyDictionary<long, object?>? computedValues = null)
    {
        var fieldData = new Dictionary<string, object?>();
        foreach (var field in fields)
        {
            // Formula fields have no physical column — take the projected value.
            if (PhysicalNaming.IsComputedTypeCode(field.TypeCode))
            {
                var cfid = (field.Fid ?? field.Id).ToString();
                fieldData[cfid] = computedValues != null && field.Fid.HasValue
                    && computedValues.TryGetValue(field.Fid.Value, out var cv) ? cv : null;
                continue;
            }

            var col = field.IsSystem && !string.IsNullOrEmpty(field.PhysicalColumnName)
                ? field.PhysicalColumnName
                : PhysicalNaming.ColumnName(field.Fid!.Value);

            if (PhysicalNaming.IsRangeTypeCode(field.TypeCode))
            {
                var endCol = PhysicalNaming.EndColumnName(field.Fid!.Value);
                var startVal = row.TryGetValue(col, out var sv) ? sv : null;
                var endVal   = row.TryGetValue(endCol, out var ev) ? ev : null;
                fieldData[(field.Fid ?? field.Id).ToString()] = new Dictionary<string, object?>
                    { ["start"] = startVal, ["end"] = endVal };
            }
            else if (row.TryGetValue(col, out var val))
            {
                // Url formula fields: override the stored URL with the computed template result.
                if (field.TypeCode == "Url" && field.Fid.HasValue
                    && FormulaTypeMap.UrlFormulaTemplate(field.Settings) != null
                    && computedValues != null && computedValues.TryGetValue(field.Fid.Value, out var urlComputed))
                {
                    fieldData[(field.Fid ?? field.Id).ToString()] = urlComputed;
                    continue;
                }

                // Resolve internal user IDs to display names for User/MultiUser and system user fields
                var fid = (field.Fid ?? field.Id).ToString();
                if (userNames != null && field.TypeCode is "User" or "MultiUser")
                {
                    fieldData[fid] = ResolveUserValue(val, userNames);
                }
                else if (userNames != null && field.IsSystem &&
                         field.PhysicalColumnName is "CreatedBy" or "ModifiedBy" &&
                         val is not null && long.TryParse(val.ToString(), out var uid))
                {
                    fieldData[fid] = userNames.TryGetValue(uid, out var name) ? name : val;
                }
                else
                {
                    if (computedValues != null && field.Fid.HasValue && computedValues.TryGetValue(field.Fid.Value, out var cv) && cv != null)
                    {
                        fieldData[fid] = cv;
                    }
                    else
                    {
                        fieldData[fid] = val;
                    }
                }
            }
        }

        var createdBy = row.TryGetValue("CreatedBy", out var cb) && cb is not null ? Convert.ToInt64(cb) : 0L;

        // Forward synthetic ActionButton formula label/color keys.
        // These are stored in computedValues with negative long keys:
        //   -(fid*2+1) = resolved ButtonLabel for the ActionButton field with that fid
        //   -(fid*2+2) = resolved ButtonColor for the ActionButton field with that fid
        // The frontend reads these as "{fid}__label" / "{fid}__color" from the record's fields.
        if (computedValues is not null)
        {
            foreach (var kv in computedValues)
            {
                if (kv.Key >= 0) continue; // positive keys are regular formula fields already handled
                // Decode: negative key → (fid, slot)
                // -(fid*2+1) ⟹ fid = (-key-1)/2, slot=label
                // -(fid*2+2) ⟹ fid = (-key-2)/2, slot=color
                var negKey = -kv.Key;
                if ((negKey - 1) % 2 == 0)
                    fieldData[$"{(negKey - 1) / 2}__label"] = kv.Value;
                else if ((negKey - 2) % 2 == 0)
                    fieldData[$"{(negKey - 2) / 2}__color"] = kv.Value;
            }
        }

        return new RecordResult
        {
            Id = (Guid)row["PublicId"]!,
            CreatedOn = (DateTime)row["CreatedOn"]!,
            ModifiedOn = row.TryGetValue("ModifiedOn", out var mo) && mo is DateTime moDate ? moDate : null,
            CreatedBy = createdBy,
            Fields = fieldData,
        };
    }

    private static object? ResolveUserValue(object? val, IReadOnlyDictionary<long, string> userNames)
    {
        if (val is null) return null;
        // MultiUser stored as JSON array of IDs
        var str = val.ToString()!;
        if (str.TrimStart().StartsWith('['))
        {
            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<long>>(str);
                if (ids != null)
                    return ids.Select(id => userNames.TryGetValue(id, out var n) ? n : id.ToString()).ToList();
            }
            catch { }
        }
        // Single user — stored as long or GUID string; try long first
        if (long.TryParse(str, out var uid))
            return userNames.TryGetValue(uid, out var name) ? name : val;
        return val;
    }
}
