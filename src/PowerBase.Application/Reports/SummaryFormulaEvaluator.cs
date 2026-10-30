using System.Globalization;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, object?>;

namespace PowerBase.Application.Reports;

/// <summary>
/// Evaluates a report's <see cref="SummaryFormula"/>s over already-grouped rows. The variables are ordinary aggregations added to the
/// same grouped query (reusing any the report already has), so there is still one pass over the data; each formula is then calculated
/// once per output row from that row's aggregate values. See <see cref="SummaryFormulaFields"/>.
/// </summary>
internal static class SummaryFormulaEvaluator
{
    private static readonly FormulaEngine Engine = new();

    internal sealed class Plan
    {
        /// <summary>Aggregations the variables need that the report does not already ask for — append them to the grouped query.</summary>
        public required List<SummaryAggregation> ExtraAggregations { get; init; }
        public required List<CompiledSummaryFormula> Formulas { get; init; }
        public bool IsEmpty => Formulas.Count == 0;
        public bool Has(long formulaId) => Formulas.Any(f => f.Formula.Id == formulaId);
        public SummaryFormula? Find(long formulaId) => Formulas.FirstOrDefault(f => f.Formula.Id == formulaId)?.Formula;
    }

    internal sealed record CompiledSummaryFormula(
        SummaryFormula Formula, CompiledFormula? Compiled, IReadOnlyList<(long VariableFid, string Alias)> Variables);

    /// <param name="allFields">Every field the run can read (the table's, plus report-formula virtual fields).</param>
    /// <param name="visibleFieldIds">Fids the viewer may read — a variable over a hidden field reads blank.</param>
    /// <param name="queryAggregations">The aggregations the report already puts in its grouped query.</param>
    public static Plan CreatePlan(
        ReportDefinition definition, IReadOnlyList<AppField> allFields, long tableId,
        IReadOnlySet<long> visibleFieldIds, IReadOnlyList<SummaryAggregation> queryAggregations)
    {
        var plan = new Plan { ExtraAggregations = [], Formulas = [] };
        if (definition.SummaryFormulas.Count == 0) return plan;

        var variableFields = SummaryFormulaFields.BuildVariableFields(definition.SummaryVariables, allFields, tableId);
        var schema = new AppFieldSchema(variableFields);
        var byFid = allFields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        var variables = new List<(long Fid, string Alias)>();
        var known = new HashSet<(long, string)>(queryAggregations.Select(a => (a.FieldId, a.Function)));
        for (var i = 0; i < definition.SummaryVariables.Count; i++)
        {
            var v = definition.SummaryVariables[i];
            var variableFid = SummaryFormulaFields.VariableFirstId + i;
            if (!byFid.TryGetValue(v.FieldId, out var source) || !visibleFieldIds.Contains(v.FieldId)) continue;
            if (variableFields.All(f => f.Fid != (int)variableFid)) continue;
            variables.Add((variableFid, SummaryFormulaFields.AggregateAlias(v.Function, source)));
            if (known.Add((v.FieldId, v.Function)))
                plan.ExtraAggregations.Add(new SummaryAggregation { FieldId = v.FieldId, Function = v.Function });
        }

        foreach (var f in definition.SummaryFormulas.Where(f => SummaryFormulaFields.IsSummaryFormulaFid(f.Id)))
        {
            CompiledFormula? compiled = null;
            if (!string.IsNullOrWhiteSpace(f.Expression)
                && FormulaTypeMap.ExpressionAndType(f.TypeCode, ReportFormulaFields.BuildSettings(SummaryFormulaFields.ToReportFormula(f))) is { } et)
            {
                var c = Engine.Compile(et.Expression, schema, et.Type);
                if (!c.HasErrors) compiled = c;
            }
            plan.Formulas.Add(new CompiledSummaryFormula(f, compiled, variables));
        }
        return plan;
    }

    /// <summary>Copies of <paramref name="rows"/> with one extra value per formula (key <see cref="SummaryFormulaFields.RowKey"/>).</summary>
    public static IReadOnlyList<Row> Apply(IReadOnlyList<Row> rows, Plan plan)
    {
        if (plan.IsEmpty) return rows;
        var options = EvaluationOptions.Default;
        var result = new List<Row>(rows.Count);
        foreach (var row in rows)
        {
            var copy = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
            foreach (var f in plan.Formulas)
            {
                var values = new Dictionary<long, object?>();
                foreach (var (fid, alias) in f.Variables)
                    values[fid] = row.TryGetValue(alias, out var v) ? Normalize(v) : null;

                object? value = null;
                if (f.Compiled is not null)
                {
                    try { value = Round(FormulaRawValue.ToRaw(Engine.Evaluate(f.Compiled, new ValuesRecordContext(values), options)), f.Formula); }
                    catch (FormulaEvaluationException) { value = null; }
                }
                copy[SummaryFormulaFields.RowKey(f.Formula.Id)] = value;
            }
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Aggregates arrive as decimal/double/int/long; the formula engine reads numbers as decimal.</summary>
    private static object? Normalize(object? v) => v switch
    {
        null => null,
        double d => (decimal)d,
        float f => (decimal)f,
        int or long or short or byte => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
        _ => v,
    };

    private static object? Round(object? value, SummaryFormula f) =>
        value is decimal d && f.DecimalPlaces is >= 0 and <= 10 ? Math.Round(d, f.DecimalPlaces.Value, MidpointRounding.AwayFromZero) : value;

    /// <summary>
    /// Re-sorts summarized rows by <paramref name="sort"/> when one of its entries is a summary formula — SQL can't order by those, so the
    /// whole list is ordered here instead (stable: ties keep the incoming order). Entries that don't resolve are dropped.
    /// </summary>
    public static IReadOnlyList<Row> Sort(
        IReadOnlyList<Row> rows, IReadOnlyList<SummarySortField> sort, int rowLevelCount,
        IReadOnlyDictionary<long, AppField> fieldMap)
    {
        IOrderedEnumerable<Row>? ordered = null;
        foreach (var s in sort)
        {
            var key = SortKey(s, rowLevelCount, fieldMap);
            if (key is null) continue;
            Func<Row, object?> selector = r => r.TryGetValue(key, out var v) ? v : null;
            ordered = ordered is null
                ? (s.Desc ? rows.OrderByDescending(selector, InMemorySummarizer.ValueOrder.Instance) : rows.OrderBy(selector, InMemorySummarizer.ValueOrder.Instance))
                : (s.Desc ? ordered.ThenByDescending(selector, InMemorySummarizer.ValueOrder.Instance) : ordered.ThenBy(selector, InMemorySummarizer.ValueOrder.Instance));
        }
        return ordered?.ToList() ?? rows;
    }

    private static string? SortKey(SummarySortField s, int rowLevelCount, IReadOnlyDictionary<long, AppField> fieldMap)
    {
        switch (s.Target)
        {
            case "RowLevel":
                return s.LevelIndex is { } i && i >= 0 && i < rowLevelCount ? $"GroupValue{i}" : null;
            case "Count":
                return "Count";
            case "Aggregation":
                if (s.AggregationFieldId is not { } id) return null;
                if (SummaryFormulaFields.IsSummaryFormulaFid(id)) return SummaryFormulaFields.RowKey(id);
                return fieldMap.TryGetValue(id, out var field) && !string.IsNullOrEmpty(s.AggregationFunction)
                    ? SummaryFormulaFields.AggregateAlias(s.AggregationFunction!, field)
                    : null;
            default:
                return null;
        }
    }
}
