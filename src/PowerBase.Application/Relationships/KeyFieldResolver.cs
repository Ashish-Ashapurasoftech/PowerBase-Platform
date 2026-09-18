using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Resolves a table's designated key field (Set Key feature). <see cref="AppTable.KeyFieldId"/> is
/// null for every table by default, meaning "the key is Record ID# (row Id)" — every consumer must
/// treat a null key field as "no translation needed, use Id-based logic exactly as before" so tables
/// that never opt into a custom key see zero behavior change.
/// </summary>
public static class KeyFieldResolver
{
    /// <summary>The table's key field, or null when the key is the default Record ID#.</summary>
    public static async Task<AppField?> ResolveAsync(AppTable table, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        if (table.KeyFieldId is not long keyFieldId) return null;
        return await fieldRepo.GetByIdInTableAsync(keyFieldId, table.Id, ct);
    }

    /// <summary>
    /// The parent field whose value the reference picker, grid, and filter surface for one
    /// specific relationship: the relationship's own <see cref="Relationship.DisplayKeyFieldId"/>
    /// override, else the parent table's global <see cref="AppTable.KeyFieldId"/>, else null
    /// (the default Record ID#, i.e. the stored row Id itself — no translation needed).
    /// The reference column always stores the parent row Id regardless of what this returns.
    /// </summary>
    public static AppField? ResolveDisplayKey(Relationship? rel, AppTable parent, IReadOnlyList<AppField> parentFields)
    {
        if (rel?.DisplayKeyFieldId is long dkId)
        {
            var dk = parentFields.FirstOrDefault(f => f.Id == dkId);
            if (dk is not null) return dk;
        }
        if (parent.KeyFieldId is long kId)
        {
            var k = parentFields.FirstOrDefault(f => f.Id == kId);
            if (k is not null) return k;
        }
        return null;
    }

    /// <summary>Async convenience for callers that don't already hold the parent's field list.</summary>
    public static async Task<AppField?> ResolveDisplayKeyAsync(
        Relationship? rel, AppTable parent, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        var parentFields = await fieldRepo.ListByTableAsync(parent.Id, ct);
        return ResolveDisplayKey(rel, parent, parentFields);
    }

    /// <summary>The physical column name backing the table's key ("Id" for the default Record ID# key).</summary>
    public static string ColumnName(AppField? keyField) =>
        keyField is null ? "Id" : (keyField.PhysicalColumnName ?? PhysicalNaming.ColumnName(keyField.Fid!.Value));

    /// <summary>Converts a raw submitted/stored text representation of a key value into the CLR type
    /// matching the key column's SQL type, for use in typed SQL parameter comparisons (avoids comparing
    /// a string against a DECIMAL/DATE column, which risks silent format mismatches). Returns null when
    /// the text is blank or doesn't parse as the expected type.</summary>
    public static object? ConvertToColumnType(AppField? keyField, string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        if (keyField is null) return long.TryParse(rawText, out var id) ? id : null; // default key: row Id
        return keyField.TypeCode switch
        {
            "Number" or "Currency" or "Percent" or "Rating" => decimal.TryParse(rawText, out var d) ? d : null,
            "Date" or "DateTime" => DateTime.TryParse(rawText, out var dt) ? dt : null,
            _ => rawText, // Text and anything else: compare as-is
        };
    }

    /// <summary>Formats a raw key-column value (as read back from the database) into the canonical
    /// text form submitted/stored for a reference — the inverse of <see cref="ConvertToColumnType"/>.</summary>
    public static string FormatForSubmit(object? rawValue) => rawValue switch
    {
        null => string.Empty,
        DateTime dt => dt.ToString("yyyy-MM-dd"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => rawValue.ToString() ?? string.Empty,
    };
}
