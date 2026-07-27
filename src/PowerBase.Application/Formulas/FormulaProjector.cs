using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
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
        var hasButtonFormulas = fields.Any(f => f.Fid.HasValue && f.TypeCode == "ActionButton" && !string.IsNullOrWhiteSpace(f.Settings));
        if ((formulaFields.Count == 0 && !hasButtonFormulas) || rows.Count == 0)
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

        // ── Compile ActionButton formula label/color once, reuse per row ────────────
        // Uses the same schema so formulas can reference any field on this table.
        // Compiled once here; evaluated inside the row loop below.
        var JsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var compiledButtonSlots = new List<(long Fid, CompiledFormula? Label, CompiledFormula? Color)>();
        foreach (var f in fields.Where(f => f.Fid.HasValue && f.TypeCode == "ActionButton" && !string.IsNullOrWhiteSpace(f.Settings)))
        {
            ActionButtonSettings? bs = null;
            try { bs = JsonSerializer.Deserialize<ActionButtonSettings>(f.Settings!, JsonOpts); } catch { }
            if (bs is null) continue;

            var labelFid = (long)f.Fid!.Value;
            CompiledFormula? labelComp = null;
            CompiledFormula? colorComp = null;

            if (bs.ButtonLabel?.Kind == ValueSourceKinds.Formula && !string.IsNullOrWhiteSpace(bs.ButtonLabel.Formula))
            {
                var c = _engine.Compile(bs.ButtonLabel.Formula!, schema, FormulaType.Text);
                if (!c.HasErrors) labelComp = c;
            }
            if (bs.ButtonColor?.Kind == ValueSourceKinds.Formula && !string.IsNullOrWhiteSpace(bs.ButtonColor.Formula))
            {
                var c = _engine.Compile(bs.ButtonColor.Formula!, schema, FormulaType.Text);
                if (!c.HasErrors) colorComp = c;
            }

            if (labelComp is not null || colorComp is not null)
                compiledButtonSlots.Add((labelFid, labelComp, colorComp));
        }

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

            // ── ActionButton formula label/color ─────────────────────────────────────
            // Evaluate ButtonLabel.Formula and ButtonColor.Formula for each ActionButton
            // field so the frontend can render the correct label and colour without knowing
            // how to run the formula engine itself.  Results land in synthetic keys
            // "{fid}__label" / "{fid}__color" which RecordResult.FromRow forwards through
            // to the API response.  Failures fail-soft to null (identical behaviour to
            // every other formula evaluation in this projector).
            foreach (var (buttonFid, labelFormula, colorFormula) in compiledButtonSlots)
            {
                if (labelFormula is not null)
                {
                    object? lv = null;
                    try { lv = FormulaRawValue.ToRaw(_engine.Evaluate(labelFormula, evalCtx, options)); }
                    catch (FormulaEvaluationException) { lv = null; }
                    map[buttonFid] = lv;                          // overwrite with computed value
                    map[-(buttonFid * 2 + 1)] = lv;              // synthetic label slot
                }
                if (colorFormula is not null)
                {
                    object? cv = null;
                    try { cv = FormulaRawValue.ToRaw(_engine.Evaluate(colorFormula, evalCtx, options)); }
                    catch (FormulaEvaluationException) { cv = null; }
                    map[-(buttonFid * 2 + 2)] = cv;              // synthetic color slot
                }
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
