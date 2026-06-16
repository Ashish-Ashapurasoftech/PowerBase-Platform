using System.Globalization;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Compute-on-read projector. Compiles each Formula field once per call (cached for
/// the request) and evaluates it per row. Runtime errors fail soft to null so a
/// single bad formula never breaks a record listing.
/// </summary>
public sealed class FormulaProjector : IFormulaProjector
{
    private static readonly IReadOnlyDictionary<long, object?> EmptyMap = new Dictionary<long, object?>();

    private readonly FormulaEngine _engine;
    private readonly IQueryContext _queryContext;

    public FormulaProjector(FormulaEngine engine, IQueryContext queryContext)
    {
        _engine = engine;
        _queryContext = queryContext;
    }

    public IReadOnlyList<IReadOnlyDictionary<long, object?>> Project(
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var formulaFields = fields.Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings)).ToList();
        if (formulaFields.Count == 0 || rows.Count == 0)
            return rows.Select(_ => EmptyMap).ToList();

        var schema = new AppFieldSchema(fields);

        // Compile each formula once; a formula with compile errors evaluates to null.
        var compiled = new List<(long Fid, CompiledFormula? Formula)>(formulaFields.Count);
        foreach (var f in formulaFields)
        {
            CompiledFormula? formula = null;
            if (f.TypeCode == "Url")
            {
                var tpl = FormulaTypeMap.UrlFormulaTemplate(f.Settings);
                if (!string.IsNullOrWhiteSpace(tpl))
                {
                    var c = _engine.Compile(tpl!, schema, FormulaType.Text);
                    formula = c.HasErrors ? null : c;
                }
            }
            else
            {
                var settings = FormulaTypeMap.ParseSettings(f.Settings);
                if (!string.IsNullOrWhiteSpace(settings?.Expression))
                {
                    var c = _engine.Compile(settings!.Expression!, schema, FormulaTypeMap.ResultType(settings.ResultType));
                    formula = c.HasErrors ? null : c;
                }
            }
            compiled.Add(((long)f.Fid!.Value, formula));
        }

        var compiledDict = compiled.ToDictionary(c => c.Fid);
        var sorted = new List<(long Fid, CompiledFormula? Formula)>(compiled.Count);
        var visited = new HashSet<long>();
        var visiting = new HashSet<long>();

        void Visit(long fid)
        {
            if (visited.Contains(fid)) return;
            if (visiting.Contains(fid)) return;

            visiting.Add(fid);

            if (compiledDict.TryGetValue(fid, out var item))
            {
                if (item.Formula != null)
                {
                    foreach (var depFid in item.Formula.ReferencedFieldIds)
                    {
                        Visit(depFid);
                    }
                }

                visiting.Remove(fid);
                visited.Add(fid);
                sorted.Add(item);
            }
            else
            {
                visiting.Remove(fid);
                visited.Add(fid);
            }
        }

        foreach (var item in compiled)
        {
            Visit(item.Fid);
        }

        var options = BuildOptions();
        var fidToColMap = fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value, f => f.PhysicalColumnName ?? string.Empty);

        var output = new List<IReadOnlyDictionary<long, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var ctx = new RowRecordContext(row, fidToColMap);
            var map = new Dictionary<long, object?>(sorted.Count);
            var projCtx = new ProjectorRecordContext(ctx, map);

            foreach (var (fid, formula) in sorted)
            {
                object? value = null;
                if (formula is not null)
                {
                    try { value = FormulaRawValue.ToRaw(_engine.Evaluate(formula, projCtx, options)); }
                    catch (FormulaEvaluationException) { value = null; }
                }
                map[fid] = value;
            }
            output.Add(map);
        }
        return output;
    }

    private EvaluationOptions BuildOptions() => new()
    {
        UtcNow = DateTime.UtcNow,
        CurrentUser = _queryContext.UserId > 0
            ? new UserRef(_queryContext.UserId.ToString(CultureInfo.InvariantCulture), _queryContext.UserEmail)
            : null,
    };

    private sealed class ProjectorRecordContext : IRecordContext
    {
        private readonly IRecordContext _inner;
        private readonly Dictionary<long, object?> _computed;

        public ProjectorRecordContext(IRecordContext inner, Dictionary<long, object?> computed)
        {
            _inner = inner;
            _computed = computed;
        }

        public object? GetValue(long fid)
        {
            if (_computed.TryGetValue(fid, out var v)) return v;
            return _inner.GetValue(fid);
        }
    }
}
