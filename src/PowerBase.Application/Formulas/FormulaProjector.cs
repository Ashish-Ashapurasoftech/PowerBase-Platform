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
        var formulaFields = fields.Where(f => f.Fid.HasValue && PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
        if (formulaFields.Count == 0 || rows.Count == 0)
            return rows.Select(_ => EmptyMap).ToList();

        var schema = new AppFieldSchema(fields);

        // Compile each formula once; a formula with compile errors evaluates to null.
        var compiled = new List<(long Fid, CompiledFormula? Formula)>(formulaFields.Count);
        foreach (var f in formulaFields)
        {
            var settings = FormulaTypeMap.ParseSettings(f.Settings);
            CompiledFormula? formula = null;
            if (!string.IsNullOrWhiteSpace(settings?.Expression))
            {
                var c = _engine.Compile(settings!.Expression!, schema, FormulaTypeMap.ResultType(settings.ResultType));
                formula = c.HasErrors ? null : c;
            }
            compiled.Add(((long)f.Fid!.Value, formula));
        }

        var options = BuildOptions();
        var output = new List<IReadOnlyDictionary<long, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var ctx = new RowRecordContext(row);
            var map = new Dictionary<long, object?>(compiled.Count);
            foreach (var (fid, formula) in compiled)
            {
                object? value = null;
                if (formula is not null)
                {
                    try { value = FormulaRawValue.ToRaw(_engine.Evaluate(formula, ctx, options)); }
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
}
