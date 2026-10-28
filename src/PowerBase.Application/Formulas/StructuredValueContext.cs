using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Formatting;
using PowerBase.Domain.Entities;
using PowerBase.Formula.Evaluation;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Hands a formula the text a user sees for the field types whose stored value is structured JSON, instead
/// of the JSON itself: a Phone is stored as <c>{"number":"+91…","ext":"12"}</c> (the formula gets the number)
/// and a MultiSelect as <c>["Red","Blue"]</c> (the formula gets <c>Red, Blue</c>). Both are typed Text in a
/// formula, so without this <c>[Mobile]</c> would return the raw JSON. Values that are already plain text
/// (older rows) pass through unchanged — it reuses <see cref="DisplayValueFormatter"/>, which tolerates both shapes.
///
/// A File field (or a lookup of one) is handed over as its file name(s): the name of each attachment, comma-joined, as the report shows them.
/// An Address is not converted here — the engine reads it whole or by part ([Address : City]) from the stored JSON, and a lookup of an address
/// is handed over as the whole address text.
///
/// Wraps the innermost single-record context only: put it directly around the row/values context, inside any
/// cross-table wrapper, so the cross-table members still reach their own context.
/// </summary>
internal sealed class StructuredValueContext : IRecordContext
{
    private readonly IRecordContext _inner;
    private readonly IReadOnlyDictionary<long, string> _typeByFid;

    public StructuredValueContext(IRecordContext inner, IReadOnlyList<AppField> fields)
    {
        _inner = inner;
        _typeByFid = fields
            .Where(f => f.Fid.HasValue)
            .Select(f => (Fid: (long)f.Fid!.Value, Kind: StructuredKind(f)))
            .Where(x => x.Kind is not null)
            .ToDictionary(x => x.Fid, x => x.Kind!);
    }

    public object? GetValue(long fid)
    {
        var value = _inner.GetValue(fid);
        if (value is null || !_typeByFid.TryGetValue(fid, out var typeCode)) return value;

        var text = value is JsonElement json ? json.ToString() : Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text)) return value;
        return typeCode switch
        {
            "Phone" => DisplayValueFormatter.FormatPhone(text),
            "File" => DisplayValueFormatter.FormatFiles(text),
            "Address" => DisplayValueFormatter.FormatAddress(text),
            _ => DisplayValueFormatter.FormatMultiSelect(text),
        };
    }

    /// <summary>The kind of structured value a field holds: Phone, MultiSelect, File, or (for a lookup) the kind it pulls down. Null for a plain field.</summary>
    private static string? StructuredKind(AppField f)
    {
        var type = f.TypeCode == "Lookup" ? Relationships.SummaryLookupSources.Settings(f)?.SourceTypeCode : f.TypeCode;
        return type is "Phone" or "MultiSelect" or "File" || (type == "Address" && f.TypeCode == "Lookup") ? type : null;
    }
}
