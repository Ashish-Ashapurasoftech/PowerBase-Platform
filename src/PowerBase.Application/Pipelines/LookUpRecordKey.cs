using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// What a Look Up a Record step searches by: the record's Record ID# (the default, and every step saved before
/// <c>lookupFieldId</c> existed) or the table's key field (Set Key), as Quickbase looks a record up by its table's key.
/// </summary>
public static class LookUpRecordKey
{
    /// <summary>The key field the step looks up by, or null for Record ID#. <paramref name="lookupFieldId"/> is "fid_N"
    /// (as the editor stores fields) or N; blank means Record ID#. A field that is no longer the table's key is an error
    /// rather than a search on a field whose values may repeat.</summary>
    public static AppField? Field(string? lookupFieldId, AppTable table, IReadOnlyList<AppField> fields)
    {
        if (string.IsNullOrWhiteSpace(lookupFieldId))
            return null;
        var text = lookupFieldId.Trim();
        if (text.StartsWith("fid_", StringComparison.OrdinalIgnoreCase))
            text = text[4..];
        if (!int.TryParse(text, out var fid))
            throw new InvalidOperationException($"Look up field '{lookupFieldId}' is not a field of table '{table.Name}'.");

        var field = fields.FirstOrDefault(f => f.Fid == fid)
            ?? throw new InvalidOperationException($"Look up field '{lookupFieldId}' is not a field of table '{table.Name}'.");
        if (IsRecordId(field))
            return null;
        if (table.KeyFieldId != field.Id)
            throw new InvalidOperationException($"'{field.Label ?? field.Name}' is no longer the key field of table '{table.Name}'; choose the look up field again.");
        return field;
    }

    public static bool IsRecordId(AppField field) =>
        field.IsSystem && string.Equals(field.PhysicalColumnName, "Id", StringComparison.OrdinalIgnoreCase);
}
