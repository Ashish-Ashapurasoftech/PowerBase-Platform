using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// A Reference converted from an existing field keeps the values that matched no parent record in a hidden
/// "(original)" field (see <c>ReferenceSettings.OriginalFid</c>). For display-only reads (report grid, export) a row
/// whose reference is NULL shows that original text in place of the blank — never for the Add/Edit form, which
/// would post the text back and be refused by <see cref="ReferenceWriteValidator"/>.
/// </summary>
public static class ReferenceOriginalFallback
{
    /// <summary>Returns <paramref name="relational"/> itself when nothing applies; otherwise a copy where the
    /// affected rows' maps carry the original text under the reference field's Fid.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<long, object?>> Apply(
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<IReadOnlyDictionary<long, object?>> relational)
    {
        List<(int RefFid, string RefCol, string OriginalCol)>? targets = null;
        foreach (var field in fields)
        {
            if (field.TypeCode != "Reference" || !field.Fid.HasValue) continue;
            if (FormulaTypeMap.ParseReferenceSettings(field.Settings)?.OriginalFid is not int originalFid) continue;
            // The original column only exists in the rows when its field was part of the read.
            if (!fields.Any(f => f.Fid == originalFid)) continue;
            (targets ??= []).Add((field.Fid.Value, PhysicalNaming.ColumnName(field.Fid.Value), PhysicalNaming.ColumnName(originalFid)));
        }
        if (targets is null) return relational;

        Dictionary<long, object?>?[]? copies = null;
        for (var i = 0; i < rows.Count && i < relational.Count; i++)
        {
            foreach (var (refFid, refCol, originalCol) in targets)
            {
                if (rows[i].TryGetValue(refCol, out var stored) && stored is not null) continue;
                if (!rows[i].TryGetValue(originalCol, out var original) || original?.ToString() is not { Length: > 0 } text) continue;

                copies ??= new Dictionary<long, object?>?[rows.Count];
                copies[i] ??= new Dictionary<long, object?>(relational[i]);
                copies[i]![refFid] = text;
            }
        }
        if (copies is null) return relational;

        var result = new IReadOnlyDictionary<long, object?>[relational.Count];
        for (var i = 0; i < result.Length; i++) result[i] = copies.ElementAtOrDefault(i) ?? relational[i];
        return result;
    }
}
