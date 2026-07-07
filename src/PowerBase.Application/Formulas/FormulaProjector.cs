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
    private readonly IFormulaRuntimeContext _runtime;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAppRepository _appRepo;

    public FormulaProjector(
        FormulaEngine engine, IQueryContext queryContext, IFormulaRuntimeContext runtime,
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo,
        IAppRepository appRepo)
    {
        _engine = engine;
        _queryContext = queryContext;
        _runtime = runtime;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _appRepo = appRepo;
    }

    public IReadOnlyList<IReadOnlyDictionary<long, object?>> Project(
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<IReadOnlyDictionary<long, object?>>? seed = null,
        AppTable? table = null)
    {
        var formulaFields = fields.Where(f => f.Fid.HasValue && FormulaTypeMap.IsFormulaComputed(f.TypeCode, f.Settings)).ToList();
        if (formulaFields.Count == 0 || rows.Count == 0)
            return seed ?? rows.Select(_ => EmptyMap).ToList();

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

        var options = BuildOptions(table);
        // Shared across rows so cross-table (GetRecords/…) metadata is resolved once and budgeted.
        var crossTable = new CrossTableQueryContext(_tableRepo, _fieldRepo, _recordRepo, table);
        var fidToColMap = fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value, f => f.PhysicalColumnName ?? string.Empty);

        var output = new List<IReadOnlyDictionary<long, object?>>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var ctx = new RowRecordContext(row, fidToColMap);
            // Seed with relational (Lookup/Summary) values so the final map carries them through
            // AND formulas can reference a lookup's value.
            var map = seed != null && i < seed.Count
                ? new Dictionary<long, object?>(seed[i])
                : new Dictionary<long, object?>(sorted.Count);
            var projCtx = new ProjectorRecordContext(ctx, map);
            var evalCtx = new CrossTableRecordContext(projCtx, crossTable);

            foreach (var (fid, formula) in sorted)
            {
                object? value = null;
                if (formula is not null)
                {
                    try { value = FormulaRawValue.ToRaw(_engine.Evaluate(formula, evalCtx, options)); }
                    catch (FormulaEvaluationException) { value = null; }
                }
                map[fid] = value;
            }
            output.Add(map);
        }
        return output;
    }

    private EvaluationOptions BuildOptions(AppTable? table) => new()
    {
        UtcNow = DateTime.UtcNow,
        CurrentUser = _queryContext.UserId > 0
            ? new UserRef(_queryContext.UserId.ToString(CultureInfo.InvariantCulture), _queryContext.UserEmail)
            : null,
        // AppID()/Dbid() surface route-usable identifiers (publicIds), so URL-formula fields can
        // build links like /app/{AppId}/tables/{Dbid()}/records/new. Blocking here matches the
        // existing pattern in CrossTableQueryContext (the evaluator itself is synchronous).
        AppId = table is null ? string.Empty : Block(_appRepo.GetPublicIdByIdAsync(table.AppId)).ToString(),
        TableId = table?.PublicId.ToString() ?? string.Empty,
        UrlRoot = _runtime.UrlRoot,
        ReturnUrl = _runtime.ReturnUrl,
    };

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();

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
