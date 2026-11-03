using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using Computed = System.Collections.Generic.IReadOnlyDictionary<long, object?>;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, object?>;

namespace PowerBase.Application.Reports;

/// <summary>
/// Summary/Chart grouping for reports that touch a computed field (Formula, Lookup, Summary). Such a field
/// has no column, so IRecordRepository.SummarizeAsync's SQL GROUP BY / SUM(...) over it fails with "Invalid
/// column name" and the whole report fails to load. Here the same result is produced in memory instead:
///   1. the table is read in batches of <see cref="BatchSize"/> rows (keyset paging by Id),
///   2. each batch gets its Lookup/Summary and Formula values projected — exactly as a record read or a
///      Table report does,
///   3. every row is added to a running accumulator for its group, and the batch is then discarded.
/// Memory therefore grows with the number of groups, never with the number of records, and there is no row
/// cap. Rows come back in the shape SummarizeAsync returns (GroupValue{i}, SeriesValue, Count and one
/// "{Function}_{FieldName}" column per aggregation), so RunSummaryAsync/ExportSummaryAsync shape them as before.
/// Reports that touch no computed field keep using the SQL path; this one mirrors its semantics (bucket
/// expressions, CAST(... AS DECIMAL(18,4)), blank-as-zero, case-insensitive grouping, NULL-first ordering).
/// </summary>
internal static class InMemorySummarizer
{
    /// <summary>Rows read per round trip — a batch size, NOT a limit on the report: every matching record is
    /// still read. Kept well under SQL Server's 2,100-parameter cap (the relationship projector looks a batch's
    /// parents/children up with IN lists) and under the formula engine's 500-query budget per projection
    /// (CrossTableQueryContext), so a formula making one cross-table call per row is never starved mid-batch.</summary>
    private const int BatchSize = 500;

    private static bool IsComputed(AppField f) => f.Fid.HasValue && PhysicalNaming.IsComputedTypeCode(f.TypeCode);

    /// <summary>True when the SQL path can't serve this report: a group-by level, the crosstab field, an
    /// aggregated field, or a filter condition is a computed field.</summary>
    public static bool IsRequired(
        IReadOnlyList<(AppField Field, string Mode)> groupByFields,
        IReadOnlyList<SummaryAggregation> aggregations,
        AppField? seriesField,
        IReadOnlyList<AppField> allFields,
        FilterGroup? filterTree)
    {
        if (groupByFields.Any(g => IsComputed(g.Field)) || (seriesField is not null && IsComputed(seriesField)))
            return true;

        var computedFids = allFields.Where(IsComputed).Select(f => (long)f.Fid!.Value).ToHashSet();
        if (computedFids.Count == 0) return false;
        return aggregations.Any(a => computedFids.Contains(a.FieldId))
            || FormulaFilterSorter.TreeContainsFormulaField(filterTree, computedFids);
    }

    public static async Task<IReadOnlyList<Row>> SummarizeAsync(
        IRecordRepository recordRepo,
        IRelationalProjector relationalProjector,
        IFormulaProjector formulaProjector,
        AppTable table,
        IReadOnlyList<(AppField Field, string Mode)> groupByFields,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyList<AppField> allFields,
        FilterGroup? filterTree,
        long? restrictToCreatedBy,
        AppField? seriesField,
        string seriesMode,
        IReadOnlyList<SummarizeSortSpec>? sort,
        CancellationToken ct)
    {
        if (groupByFields.Count == 0) return [];

        var fieldMap = allFields.Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // Conditions on a computed field (or an encrypted one — SQL would compare ciphertext) can't run in
        // SQL: they are applied in memory to each projected batch instead.
        var inMemoryFids = allFields.Where(f => f.Fid.HasValue && (IsComputed(f) || f.IsEncrypted))
            .Select(f => (long)f.Fid!.Value).ToHashSet();
        var (physicalFilter, memoryFilter) = FormulaFilterSorter.SplitFilterTree(filterTree, inMemoryFids);

        var dimensions = new List<Dimension>(groupByFields.Count + 1);
        foreach (var (field, mode) in groupByFields) dimensions.Add(new Dimension(field, mode));
        if (seriesField is not null) dimensions.Add(new Dimension(seriesField, seriesMode));
        var levelCount = groupByFields.Count;

        // Parallel to `aggregations`; null = unknown field, skipped exactly as the SQL path skips it.
        var specs = new List<AggregationSpec?>(aggregations.Count);
        foreach (var agg in aggregations)
            specs.Add(fieldMap.TryGetValue(agg.FieldId, out var aggField) ? new AggregationSpec(aggField, agg.Function) : null);

        var groups = new Dictionary<GroupKey, GroupState>(GroupKeyComparer.Instance);
        long afterId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await recordRepo.ListBatchAfterIdAsync(table, allFields, afterId, BatchSize, physicalFilter, restrictToCreatedBy, ct);
            if (batch.Count == 0) break;
            afterId = Convert.ToInt64(batch[^1]["Id"], CultureInfo.InvariantCulture);

            var relational = await relationalProjector.ProjectAsync(table, allFields, batch, ct);
            var computed = formulaProjector.Project(allFields, batch, relational, table);

            IEnumerable<(Row Row, Computed Computed)> pairs = batch.Zip(computed, (r, c) => (Row: r, Computed: c));
            if (memoryFilter is not null)
                pairs = FormulaFilterSorter.ApplyFormulaFilters(pairs, memoryFilter, allFields);

            foreach (var (row, comp) in pairs)
            {
                var keys = new object?[dimensions.Count];
                for (var d = 0; d < keys.Length; d++) keys[d] = dimensions[d].ValueOf(row, comp);

                var key = new GroupKey(keys);
                if (!groups.TryGetValue(key, out var state))
                {
                    state = new GroupState(keys, specs.Select(s => s?.CreateAccumulator()).ToArray());
                    groups[key] = state;
                }

                state.Count++;
                for (var a = 0; a < specs.Count; a++)
                    if (state.Accumulators[a] is { } acc) acc.Add(specs[a]!.Read(row, comp));
            }

            if (batch.Count < BatchSize) break;
        }

        foreach (var g in groups.Values)
            g.Results = g.Accumulators.Select(a => a?.Result()).ToArray();

        // Default order: ascending by every group level, then the crosstab field — NULL first, like SQL.
        IEnumerable<GroupState> ordered = groups.Values;
        for (var d = 0; d < dimensions.Count; d++)
        {
            var idx = d;
            ordered = d == 0
                ? ordered.OrderBy(g => g.Keys[idx], ValueOrder.Instance)
                : ((IOrderedEnumerable<GroupState>)ordered).ThenBy(g => g.Keys[idx], ValueOrder.Instance);
        }

        // A caller-supplied sort replaces the default (stable, so ties keep the default order); entries that
        // don't resolve are dropped, as in the SQL path.
        if (sort is { Count: > 0 })
        {
            IOrderedEnumerable<GroupState>? sorted = null;
            foreach (var s in sort)
            {
                Func<GroupState, object?>? selector = s.Target switch
                {
                    SummarizeSortTarget.GroupLevel when s.Index >= 0 && s.Index < levelCount => g => g.Keys[s.Index],
                    SummarizeSortTarget.Count => g => g.Count,
                    SummarizeSortTarget.Aggregation when s.Index >= 0 && s.Index < specs.Count && specs[s.Index] is not null
                        && !specs[s.Index]!.IsSkipped => g => g.Results![s.Index],
                    _ => null,
                };
                if (selector is null) continue;
                sorted = sorted is null
                    ? (s.Desc ? ordered.OrderByDescending(selector, ValueOrder.Instance) : ordered.OrderBy(selector, ValueOrder.Instance))
                    : (s.Desc ? sorted.ThenByDescending(selector, ValueOrder.Instance) : sorted.ThenBy(selector, ValueOrder.Instance));
            }
            if (sorted is not null) ordered = sorted;
        }

        var result = new List<Row>(groups.Count);
        foreach (var g in ordered)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < levelCount; i++) row[$"GroupValue{i}"] = g.Keys[i];
            if (seriesField is not null) row["SeriesValue"] = g.Keys[levelCount];
            row["Count"] = g.Count;
            for (var a = 0; a < specs.Count; a++)
                if (specs[a] is { IsSkipped: false } spec) row[spec.Alias] = g.Results![a];
            result.Add(row);
        }
        return result;
    }

    // ── Reading values ──────────────────────────────────────────────────────────

    /// <summary>A computed field's value comes from the projected map; a stored one from its column.</summary>
    private static Func<Row, Computed, object?> ReaderFor(AppField f)
    {
        var fid = (long)f.Fid!.Value;
        if (IsComputed(f)) return (_, c) => c.TryGetValue(fid, out var v) ? v : null;

        var col = f.IsSystem && !string.IsNullOrEmpty(f.PhysicalColumnName)
            ? f.PhysicalColumnName!
            : PhysicalNaming.ColumnName(f.Fid!.Value);
        return (r, _) => r.TryGetValue(col, out var v) ? v : null;
    }

    /// <summary>A list-valued formula (users, multi-select) groups/counts as its ", "-joined text.</summary>
    private static object? Flatten(object? v) =>
        v is System.Collections.IEnumerable and not string and not byte[]
            ? string.Join(", ", ((System.Collections.IEnumerable)v).Cast<object?>().Select(i => i?.ToString()))
            : v;

    private static decimal? ToDecimal(object? v)
    {
        switch (v)
        {
            case null: return null;
            case decimal d: return d;
            case bool b: return b ? 1m : 0m;
            case string s: return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : null;
            case IConvertible c:
                try { return Convert.ToDecimal(c, CultureInfo.InvariantCulture); }
                catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; }
            default: return null;
        }
    }

    /// <summary>Stored dates arrive as DateTime; a formula's date/datetime as ISO text.</summary>
    private static DateTime? ToDate(object? v) => v switch
    {
        null => null,
        DateTime d => d,
        DateTimeOffset o => o.UtcDateTime,
        string s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? p : null,
        _ => null,
    };

    private static bool? ToBool(object? v) => v switch
    {
        null => null,
        bool b => b,
        string s => bool.TryParse(s, out var p) ? p : null,
        _ => ToDecimal(v) is { } d ? d != 0m : null,
    };

    /// <summary>CAST(x AS DECIMAL(18,4)) — what the SQL path applies before SUM/AVG/STDEV/MEDIAN.</summary>
    private static decimal Quantize(decimal d) => Math.Round(d, 4, MidpointRounding.AwayFromZero);

    /// <summary>Number/Currency/Percent/Rating's "Treat blank values as 0 in calculations" setting — defaults
    /// to true when unset (same rule as RecordRepository.TreatBlankAsZero).</summary>
    private static bool TreatBlankAsZero(AppField field)
    {
        if (string.IsNullOrWhiteSpace(field.Settings)) return true;
        try
        {
            return JsonSerializer.Deserialize<NumericSettings>(
                field.Settings, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.TreatBlankAsZero ?? true;
        }
        catch (JsonException) { return true; }
    }

    // ── Group-by dimensions ─────────────────────────────────────────────────────

    /// <summary>One group-by level (or the crosstab field): reads the value, then applies the same
    /// bucketing RecordRepository.BuildGroupByExpr does in SQL.</summary>
    private sealed class Dimension
    {
        private static readonly DateTime WeekEpoch = new(1900, 1, 1);

        private readonly Func<Row, Computed, object?> _read;
        private readonly GroupByModeCategoryHelper.GroupByFamily _family;
        private readonly string _mode;

        public Dimension(AppField field, string mode)
        {
            _read = ReaderFor(field);
            _family = GroupByModeCategoryHelper.GetFamily(field.TypeCode);
            _mode = string.IsNullOrWhiteSpace(mode) ? "EqualValues" : mode;
        }

        public object? ValueOf(Row row, Computed comp)
        {
            var v = Flatten(_read(row, comp));
            if (v is null) return null;

            switch (_family)
            {
                case GroupByModeCategoryHelper.GroupByFamily.TextRich:
                case GroupByModeCategoryHelper.GroupByFamily.User:
                {
                    if (_mode is not ("FirstWord" or "FirstLetter")) return v;
                    var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (_mode == "FirstLetter") return s.Length > 0 ? s[..1] : s;
                    var space = s.IndexOf(' ');
                    return space >= 0 ? s[..space] : s;
                }

                case GroupByModeCategoryHelper.GroupByFamily.DateFamily:
                {
                    if (ToDate(v) is not { } date) return v;
                    return _mode switch
                    {
                        "Day" => date.Date,
                        // DATEADD(WEEK, DATEDIFF(WEEK, 0, col), 0): week boundaries are Sundays (SQL's default
                        // DATEFIRST), counted from 1900-01-01.
                        "Week" => WeekEpoch.AddDays(7 * FloorDiv((date.Date - WeekEpoch).Days + 1, 7)),
                        "Month" => new DateTime(date.Year, date.Month, 1),
                        "Quarter" => new DateTime(date.Year, ((date.Month - 1) / 3) * 3 + 1, 1),
                        "Year" => new DateTime(date.Year, 1, 1),
                        "Decade" => new DateTime(date.Year / 10 * 10, 1, 1),
                        _ => date,
                    };
                }

                case GroupByModeCategoryHelper.GroupByFamily.DurationFamily:
                {
                    if (ToDecimal(v) is not { } minutes) return v;
                    // Stored in whole minutes, so "Minute" is the raw value. Truncate = SQL integer division.
                    return _mode switch
                    {
                        "Hour" => Math.Truncate(minutes / 60m) * 60m,
                        "Day" => Math.Truncate(minutes / 1440m) * 1440m,
                        "Week" => Math.Truncate(minutes / 10080m) * 10080m,
                        _ => minutes,
                    };
                }

                case GroupByModeCategoryHelper.GroupByFamily.Numeric:
                {
                    if (ToDecimal(v) is not { } number) return v;
                    return _mode switch
                    {
                        "Increment1" => Math.Floor(number / 1m) * 1m,
                        "Increment10" => Math.Floor(number / 10m) * 10m,
                        "Increment100" => Math.Floor(number / 100m) * 100m,
                        "Increment1000" => Math.Floor(number / 1000m) * 1000m,
                        "Increment10000" => Math.Floor(number / 10000m) * 10000m,
                        _ => number,
                    };
                }

                default:
                    // TextSimple, Boolean, Time, MultiUser, NoGrouping (Formula…), Unclassified: raw value.
                    return v;
            }
        }

        private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
    }

    // ── Aggregations ────────────────────────────────────────────────────────────

    private sealed class AggregationSpec
    {
        private readonly Func<Row, Computed, object?> _read;
        private readonly string _function;
        private readonly FieldTypeCategoryHelper.SummarizableCategory _category;
        private readonly bool _isBool;
        private readonly bool _blankAsZero;

        public AggregationSpec(AppField field, string function)
        {
            _function = function;
            _read = ReaderFor(field);
            _blankAsZero = TreatBlankAsZero(field);

            var resultType = SummaryReportConfigValidator.GetFormulaResultType(field);
            _category = FieldTypeCategoryHelper.GetSummarizableCategory(field.TypeCode, resultType);
            _isBool = field.TypeCode is "Boolean" or "Formula_Bool"
                || (field.TypeCode == "Formula" && string.Equals(resultType, "Bool", StringComparison.OrdinalIgnoreCase));

            Alias = $"{function}_{field.Name.Replace(" ", "_")}";
            IsSkipped = CreateAccumulator() is null;
        }

        /// <summary>The "{Function}_{FieldName}" key SQL names this column — and the key the rows carry.</summary>
        public string Alias { get; }

        /// <summary>No accumulator for this function/type — the SQL path emits no column for it either.</summary>
        public bool IsSkipped { get; }

        public object? Read(Row row, Computed comp) => Flatten(_read(row, comp));

        public Accumulator? CreateAccumulator()
        {
            var numeric = _category == FieldTypeCategoryHelper.SummarizableCategory.Numeric;
            return _function switch
            {
                "Sum" when numeric => new SumAvgAccumulator(average: false, _blankAsZero),
                "Avg" when numeric => new SumAvgAccumulator(average: true, _blankAsZero),
                "StdDev" when numeric => new StdDevAccumulator(),
                "Median" when numeric => new MedianAccumulator(),
                "Min" or "Max" when numeric => new ExtremeAccumulator(_function == "Max", _isBool ? ExtremeKind.Bool : ExtremeKind.Number),
                "Min" or "Max" when _category == FieldTypeCategoryHelper.SummarizableCategory.Date
                    => new ExtremeAccumulator(_function == "Max", ExtremeKind.Date),
                "DistinctCount" => new DistinctAccumulator(),
                _ => null,
            };
        }
    }

    private abstract class Accumulator
    {
        public abstract void Add(object? value);
        public abstract object? Result();
    }

    /// <summary>SUM / AVG of DECIMAL(18,4)-cast values. A blank counts as 0 (in the average's denominator
    /// too) when the field's "treat blank as 0" setting is on, and is skipped otherwise.</summary>
    private sealed class SumAvgAccumulator : Accumulator
    {
        private readonly bool _average;
        private readonly bool _blankAsZero;
        private decimal _sum;
        private long _count;

        public SumAvgAccumulator(bool average, bool blankAsZero) { _average = average; _blankAsZero = blankAsZero; }

        public override void Add(object? value)
        {
            var d = ToDecimal(value);
            if (d is null)
            {
                if (!_blankAsZero) return;
                d = 0m;
            }
            _sum += Quantize(d.Value);
            _count++;
        }

        public override object? Result() =>
            _count == 0 ? null : _average ? Math.Round(_sum / _count, 6, MidpointRounding.AwayFromZero) : _sum;
    }

    /// <summary>Sample standard deviation (STDEV) by Welford's single-pass method; NULL for fewer than 2 values.</summary>
    private sealed class StdDevAccumulator : Accumulator
    {
        private long _n;
        private double _mean;
        private double _m2;

        public override void Add(object? value)
        {
            if (ToDecimal(value) is not { } d) return;
            var x = (double)Quantize(d);
            _n++;
            var delta = x - _mean;
            _mean += delta / _n;
            _m2 += delta * (x - _mean);
        }

        public override object? Result() => _n < 2 ? null : Math.Sqrt(_m2 / (_n - 1));
    }

    /// <summary>PERCENTILE_CONT(0.5): the middle value, or the mean of the two middle ones.</summary>
    private sealed class MedianAccumulator : Accumulator
    {
        private readonly List<decimal> _values = [];

        public override void Add(object? value)
        {
            if (ToDecimal(value) is { } d) _values.Add(Quantize(d));
        }

        public override object? Result()
        {
            if (_values.Count == 0) return null;
            _values.Sort();
            var mid = _values.Count / 2;
            return _values.Count % 2 == 1
                ? (double)_values[mid]
                : ((double)_values[mid - 1] + (double)_values[mid]) / 2d;
        }
    }

    private enum ExtremeKind { Number, Date, Bool }

    /// <summary>MIN / MAX in the field's own domain (number, date, or checkbox).</summary>
    private sealed class ExtremeAccumulator : Accumulator
    {
        private readonly bool _max;
        private readonly ExtremeKind _kind;
        private IComparable? _best;

        public ExtremeAccumulator(bool max, ExtremeKind kind) { _max = max; _kind = kind; }

        public override void Add(object? value)
        {
            IComparable? v = _kind switch
            {
                ExtremeKind.Number => ToDecimal(value),
                ExtremeKind.Date => ToDate(value),
                _ => ToBool(value),
            };
            if (v is null) return;
            if (_best is null || (_max ? v.CompareTo(_best) > 0 : v.CompareTo(_best) < 0)) _best = v;
        }

        public override object? Result() => _best;
    }

    /// <summary>COUNT(DISTINCT x): blanks ignored, text compared case-insensitively like the database does.</summary>
    private sealed class DistinctAccumulator : Accumulator
    {
        private readonly HashSet<object?> _seen = new(ValueEquality.Instance);

        public override void Add(object? value)
        {
            if (value is not null) _seen.Add(value);
        }

        public override object? Result() => _seen.Count;
    }

    // ── Grouping / ordering ─────────────────────────────────────────────────────

    private sealed class GroupState
    {
        public GroupState(object?[] keys, Accumulator?[] accumulators)
        {
            Keys = keys;
            Accumulators = accumulators;
        }

        public object?[] Keys { get; }
        public Accumulator?[] Accumulators { get; }
        public long Count { get; set; }
        public object?[]? Results { get; set; }
    }

    private readonly struct GroupKey
    {
        public GroupKey(object?[] values) => Values = values;
        public object?[] Values { get; }
    }

    private sealed class GroupKeyComparer : IEqualityComparer<GroupKey>
    {
        public static readonly GroupKeyComparer Instance = new();

        public bool Equals(GroupKey x, GroupKey y)
        {
            if (x.Values.Length != y.Values.Length) return false;
            for (var i = 0; i < x.Values.Length; i++)
                if (!ValueEquality.Instance.Equals(x.Values[i], y.Values[i])) return false;
            return true;
        }

        public int GetHashCode(GroupKey key)
        {
            var hash = new HashCode();
            foreach (var v in key.Values) hash.Add(v, ValueEquality.Instance);
            return hash.ToHashCode();
        }
    }

    /// <summary>NULL equals NULL (SQL puts NULLs in one group); text ignores case (the database's default
    /// collation); everything else uses its own equality (decimal 1.0 equals 1.00).</summary>
    private sealed class ValueEquality : IEqualityComparer<object?>
    {
        public static readonly ValueEquality Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is null || y is null) return x is null && y is null;
            return x is string a && y is string b
                ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                : x.Equals(y);
        }

        public int GetHashCode(object? obj) => obj switch
        {
            null => 0,
            string s => StringComparer.OrdinalIgnoreCase.GetHashCode(s),
            _ => obj.GetHashCode(),
        };
    }

    /// <summary>ORDER BY ordering: NULL first, text case-insensitively, other values by their natural order.</summary>
    internal sealed class ValueOrder : IComparer<object?>
    {
        public static readonly ValueOrder Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (x is string a && y is string b) return string.Compare(a, b, StringComparison.InvariantCultureIgnoreCase);
            if (x is IComparable c && x.GetType() == y.GetType()) return c.CompareTo(y);
            if (ToDecimal(x) is { } dx && ToDecimal(y) is { } dy) return dx.CompareTo(dy);
            return string.Compare(x.ToString(), y.ToString(), StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
