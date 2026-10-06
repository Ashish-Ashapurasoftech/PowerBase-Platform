using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Computes a summary in memory over decrypted child rows. SQL can't match, group or aggregate
/// ciphertext, so for a summary that reads an encrypted column the caller loads the child table's
/// rows decrypted (IRecordRepository.ListAllRowsDecryptedAsync) and this class does what
/// AggregateByReferenceAsync / ListValuesByReferenceAsync do in SQL: keep the rows of the page's
/// parents that match the criteria, group them by parent, and aggregate. Only the child table's own
/// stored fields are read (SummaryEncryptionGuard refuses the rest).
/// </summary>
public static class EncryptedSummaryAggregator
{
    /// <summary>Returns parent key (see <see cref="RelationalProjector.NormalizeParentKey"/>) → value.
    /// Parents with no matching child are absent; the caller supplies their Count/Exists default.</summary>
    /// <param name="format">Renders one child value as Combined Text shows it (display formatting).</param>
    public static IReadOnlyDictionary<object, object?> Aggregate(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<AppField> childFields,
        string function, int refFid, int? targetFid, string? targetSubField,
        IReadOnlySet<string> parentKeys, FilterGroup? filter, CombinedTextOptions? options,
        Func<object?, string?> format, Func<string, FilterGroup?>? filterFor = null)
    {
        var result = new Dictionary<object, object?>();
        var fieldsByFid = childFields.Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var target = targetFid.HasValue ? fieldsByFid.GetValueOrDefault(targetFid.Value) : null;
        var sortField = options?.SortFid is int sf ? fieldsByFid.GetValueOrDefault(sf) : null;

        // Matching children, grouped by the parent they belong to.
        var groups = new Dictionary<string, List<IReadOnlyDictionary<string, object?>>>();
        foreach (var (key, row) in MatchingRows(rows, childFields, refFid, parentKeys, filterFor ?? (_ => filter)))
        {
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(row);
        }

        foreach (var (key, children) in groups)
        {
            object? value;
            switch (function)
            {
                case SummaryFunctions.Count: value = children.Count; break;
                case SummaryFunctions.Exists: value = children.Count > 0; break;
                case SummaryFunctions.CombinedText:
                    value = target is null ? null : Combine(children, target, targetSubField, sortField, options, format);
                    break;
                default:
                    value = target is null ? null : Reduce(children, function, target, targetSubField);
                    break;
            }
            if (value is not null) result[key] = value;
        }
        return result;
    }

    /// <summary>The child rows that belong to one of the page's parents (key = the parent key) and match the
    /// criteria — the part of a summary SQL does in its WHERE, here over decrypted rows.</summary>
    public static IReadOnlyList<(string Key, IReadOnlyDictionary<string, object?> Row)> MatchingRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<AppField> childFields,
        int refFid, IReadOnlySet<string> parentKeys, FilterGroup? filter)
        => MatchingRows(rows, childFields, refFid, parentKeys, _ => filter);

    /// <summary>As above, with the criteria chosen per parent (<paramref name="filterFor"/> gets the parent key) -
    /// for criteria that compare a child field to a field of its own parent.</summary>
    public static IReadOnlyList<(string Key, IReadOnlyDictionary<string, object?> Row)> MatchingRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<AppField> childFields,
        int refFid, IReadOnlySet<string> parentKeys, Func<string, FilterGroup?> filterFor)
    {
        var refCol = Column(refFid);
        var matched = new List<(string, IReadOnlyDictionary<string, object?>)>();
        foreach (var row in rows)
        {
            if (!row.TryGetValue(refCol, out var rawRef) || NormalizeKey(rawRef) is not { } key || !parentKeys.Contains(key)) continue;
            var filter = filterFor(key);
            if (filter is { Nodes.Count: > 0 } && !PipelineFilterEvaluator.EvaluateFilterGroup(filter, ValuesByFid(row, childFields), childFields)) continue;
            matched.Add((key, row));
        }
        return matched;
    }

    /// <summary>A parent key's canonical text, so a decrypted "42" or "42.0000" matches the parent's Id 42.</summary>
    internal static string? NormalizeKey(object? raw) => raw switch
    {
        null => null,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => RelationalProjector.NormalizeParentKey(d),
        _ => RelationalProjector.NormalizeParentKey(raw),
    };

    private static string Column(int fid) => EncryptedRowFilter.Column(fid);
    private static string Column(AppField f) => EncryptedRowFilter.Column(f);
    private static IReadOnlyDictionary<long, object?> ValuesByFid(IReadOnlyDictionary<string, object?> row, IReadOnlyList<AppField> fields) => EncryptedRowFilter.ValuesByFid(row, fields);

    private static object? ReadValue(IReadOnlyDictionary<string, object?> row, AppField field, string? subField)
    {
        row.TryGetValue(Column(field), out var v);
        if (v is DBNull) v = null;
        return string.IsNullOrWhiteSpace(subField) ? v : RelationalProjector.ExtractJsonSubField(v, subField);
    }

    private static string ResultKind(AppField f) => f.TypeCode switch
    {
        "Date" or "Formula_Date" => "Date",
        "DateTime" or "Formula_DateTime" => "DateTime",
        "Number" or "Currency" or "Percent" or "Rating" or "Duration" => "Number",
        _ => "Text",
    };

    private static object? Reduce(
        List<IReadOnlyDictionary<string, object?>> children, string function, AppField target, string? subField)
    {
        var kind = string.IsNullOrWhiteSpace(subField) ? ResultKind(target) : "Text";
        var values = children.Select(r => ReadValue(r, target, subField)).ToList();
        try
        {
            var value = SummaryComputedTargets.Aggregate(function, kind, values, null);
            // SQL gives a date back as a DateTime; the in-memory comparison returns the stored text.
            return kind is "Date" or "DateTime" && value is string s
                ? DateTime.Parse(s, CultureInfo.InvariantCulture)
                : value;
        }
        catch (FormatException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    private static string? Combine(
        List<IReadOnlyDictionary<string, object?>> children, AppField target, string? subField,
        AppField? sortField, CombinedTextOptions? options, Func<object?, string?> format)
    {
        var o = options ?? CombinedTextOptions.Default;
        IEnumerable<IReadOnlyDictionary<string, object?>> ordered = children;   // already in Id order
        if (sortField is not null)
        {
            var kind = ResultKind(sortField);
            IComparable? Key(IReadOnlyDictionary<string, object?> r) => ReadValue(r, sortField, null) switch
            {
                null => null,
                var v when kind == "Number" && decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                var v when kind is "Date" or "DateTime" && DateTime.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) => dt,
                var v => Convert.ToString(v, CultureInfo.InvariantCulture),
            };
            ordered = o.SortDescending
                ? children.OrderByDescending(Key, Comparer<IComparable?>.Default)
                : children.OrderBy(Key, Comparer<IComparable?>.Default);
        }
        else if (o.SortDescending) ordered = children.AsEnumerable().Reverse();

        var texts = ordered
            .Select(r => format(ReadValue(r, target, subField)))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!);
        if (o.DistinctValues) texts = texts.Distinct(StringComparer.Ordinal);
        var joined = string.Join(o.Delimiter, texts);
        return joined.Length > 0 ? joined : null;
    }
}
