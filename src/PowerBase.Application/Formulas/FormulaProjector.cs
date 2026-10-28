using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula;
using PowerBase.Formula.Binding;
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
    private readonly IAppVariableRepository? _variableRepo;
    private readonly FormulaUserDirectory? _users;
    private readonly IAppUserRepository? _appUserRepo;
    private readonly IAppRoleRepository? _appRoleRepo;

    public FormulaProjector(
        FormulaEngine engine, IQueryContext queryContext, IFormulaRuntimeContext runtime,
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo,
        IAppRepository appRepo, IAppVariableRepository? variableRepo = null, IUserRepository? userRepo = null,
        IAppUserRepository? appUserRepo = null, IAppRoleRepository? appRoleRepo = null)
    {
        _appUserRepo = appUserRepo;
        _appRoleRepo = appRoleRepo;
        _variableRepo = variableRepo;
        _users = userRepo is null ? null : new FormulaUserDirectory(userRepo);
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
        var hasButtonFormulas = fields.Any(f => f.Fid.HasValue && PhysicalNaming.IsActionButtonTypeCode(f.TypeCode) && !string.IsNullOrWhiteSpace(f.Settings));
        if ((formulaFields.Count == 0 && !hasButtonFormulas) || rows.Count == 0)
            return seed ?? rows.Select(_ => EmptyMap).ToList();

        var schema = new AppFieldSchema(fields);
        var variableSchema = LoadVariableSchema(fields, formulaFields, table);

        // Compile each formula once; a formula with compile errors evaluates to null.
        var compiled = new List<(long Fid, CompiledFormula? Formula)>(formulaFields.Count);
        foreach (var f in formulaFields)
        {
            CompiledFormula? formula = null;
            var exprAndType = FormulaTypeMap.ExpressionAndType(f.TypeCode, f.Settings);
            if (exprAndType is { } et)
            {
                var c = _engine.Compile(et.Expression, schema, et.Type, variableSchema: variableSchema);
                formula = c.HasErrors ? null : c;
            }
            compiled.Add(((long)f.Fid!.Value, formula));
        }

        var compiledDict = compiled.ToDictionary(c => c.Fid);
        var sorted = new List<(long Fid, CompiledFormula? Formula)>(compiled.Count);
        var visited = new HashSet<long>();
        var visiting = new HashSet<long>();

        // Whether formula `start` reads formula `target`, directly or through other formulas.
        bool ReadsTransitively(long start, long target)
        {
            var seen = new HashSet<long>();
            var pending = new Stack<long>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current) || !compiledDict.TryGetValue(current, out var node) || node.Formula is null) continue;
                foreach (var dep in node.Formula.ReferencedFieldIds)
                {
                    if (dep == target) return true;
                    pending.Push(dep);
                }
            }
            return false;
        }

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

                    // A formula that reads a field chosen per record can't list what it depends on, so it
                    // runs after every plain formula — except any that read its own result back.
                    if (item.Formula.UsesDynamicFieldReference)
                    {
                        foreach (var (otherFid, other) in compiledDict)
                        {
                            if (otherFid != fid && other.Formula is { UsesDynamicFieldReference: false } && !ReadsTransitively(otherFid, fid))
                                Visit(otherFid);
                        }
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

        var options = BuildOptions(table, fields);
        // Shared across rows so cross-table (GetRecords/…) metadata is resolved once and budgeted.
        var crossTable = new CrossTableQueryContext(_tableRepo, _fieldRepo, _recordRepo, table);
        var fidToColMap = fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value, f => f.PhysicalColumnName ?? string.Empty);

        // UserToName/UserToEmail need the user directory, not just the id a record stores. Load every user the
        // formulas read in one query up front instead of one blocking lookup per row.
        PrefetchUsers(compiled, fields, fidToColMap, rows);

        // ── Compile ActionButton formula label/color once, reuse per row ────────────
        // Uses the same schema so formulas can reference any field on this table.
        // Compiled once here; evaluated inside the row loop below.
        var JsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var compiledButtonSlots = new List<(long Fid, CompiledFormula? Label, CompiledFormula? Color)>();
        foreach (var f in fields.Where(f => f.Fid.HasValue && PhysicalNaming.IsActionButtonTypeCode(f.TypeCode) && !string.IsNullOrWhiteSpace(f.Settings)))
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
            var ctx = new StructuredValueContext(new RowRecordContext(row, fidToColMap), fields);
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

    /// <summary>The app's variables, when any formula here names one. A saved formula names a variable by id
    /// ([var_5|Tax]), so a table with no such formula never pays for the lookup.</summary>
    private IVariableSchema? LoadVariableSchema(IReadOnlyList<AppField> fields, IReadOnlyList<AppField> formulaFields, AppTable? table)
    {
        if (_variableRepo is null) return null;

        var usesVariable = formulaFields.Any(f =>
            FormulaTypeMap.ExpressionAndType(f.TypeCode, f.Settings) is { } et
            && et.Expression.Contains("[" + VariableIdReference.Prefix, StringComparison.OrdinalIgnoreCase));
        if (!usesVariable) return null;

        var appId = table?.AppId ?? Block(_tableRepo.GetByIdAsync(fields[0].AppTableId)).AppId;
        return new AppVariableSchema(Block(_variableRepo.ListAsync(appId)));
    }

    public IReadOnlySet<long>? GetFormulaDependencies(IReadOnlyList<AppField> fields, IEnumerable<long> formulaFids)
    {
        var schema = new AppFieldSchema(fields);
        var formulaByFid = fields
            .Where(f => f.Fid.HasValue && FormulaTypeMap.IsFormulaComputed(f.TypeCode, f.Settings))
            .ToDictionary(f => (long)f.Fid!.Value);
        var result = new HashSet<long>();
        var pending = new Stack<long>(formulaFids);
        while (pending.Count > 0)
        {
            var fid = pending.Pop();
            if (!result.Add(fid) || !formulaByFid.TryGetValue(fid, out var field)) continue;
            var exprAndType = FormulaTypeMap.ExpressionAndType(field.TypeCode, field.Settings);
            if (exprAndType is not { } et) return null;
            var compiled = _engine.Compile(et.Expression, schema, et.Type);
            if (compiled.HasErrors) return null;
            // A field chosen per record can't be listed up front — the same "unknown, project everything" answer.
            if (compiled.UsesDynamicFieldReference) return null;
            foreach (var dep in compiled.ReferencedFieldIds) pending.Push(dep);
        }
        return result;
    }

    private void PrefetchUsers(
        IReadOnlyList<(long Fid, CompiledFormula? Formula)> compiled, IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, string> fidToColMap, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (_users is null) return;

        var anyDynamic = compiled.Any(c => c.Formula is { UsesDynamicFieldReference: true });
        var referenced = compiled.Where(c => c.Formula is not null).SelectMany(c => c.Formula!.ReferencedFieldIds).ToHashSet();
        var fieldTypes = FormulaTypeMap.FieldTypesByFid(fields);

        foreach (var (fid, type) in fieldTypes)
        {
            if (type is not (FormulaType.User or FormulaType.UserList)) continue;
            if (!anyDynamic && !referenced.Contains(fid)) continue;
            // Same column resolution as RowRecordContext: the mapped physical column, else f_{fid}.
            var col = fidToColMap.TryGetValue(fid, out var mapped) && mapped.Length > 0 ? mapped : PhysicalNaming.ColumnName((int)fid);
            _users.Prefetch(rows.Select(r => r.TryGetValue(col, out var v) ? v : null));
        }
    }

    private EvaluationOptions BuildOptions(AppTable? table, IReadOnlyList<AppField> fields) => new()
    {
        UtcNow = DateTime.UtcNow,
        UserLookup = _users is null ? null : _users.Lookup,
        FieldTypes = FormulaTypeMap.FieldTypesByFid(fields),
        CurrentUserRoles = RolesOf(table, _queryContext.UserId),
        UserRolesOf = _appUserRepo is null || _appRoleRepo is null || table is null
            ? null
            : userId => long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? LoadRoles(table.AppId, id) : Array.Empty<RoleRef>(),
        CurrentUser = _queryContext.UserId > 0
            ? new UserRef(_queryContext.UserId.ToString(CultureInfo.InvariantCulture), _queryContext.UserEmail, _queryContext.UserName)
            : null,
        // AppID()/Dbid() surface route-usable identifiers (publicIds), so URL-formula fields can
        // build links like /app/{AppId}/tables/{Dbid()}/records/new. Blocking here matches the
        // existing pattern in CrossTableQueryContext (the evaluator itself is synchronous).
        AppId = table is null ? string.Empty : Block(_appRepo.GetPublicIdByIdAsync(table.AppId)).ToString(),
        TableId = table?.PublicId.ToString() ?? string.Empty,
        UrlRoot = _runtime.UrlRoot,
        ReturnUrl = _runtime.ReturnUrl,
    };

    /// <summary>The roles of a user in the table's app, for <c>UserRoles()</c>; looked up only when a formula calls it.</summary>
    private Func<IReadOnlyList<RoleRef>>? RolesOf(AppTable? table, long userId)
    {
        if (_appUserRepo is null || _appRoleRepo is null || table is null || userId <= 0) return null;
        IReadOnlyList<RoleRef>? loaded = null;
        return () => loaded ??= LoadRoles(table.AppId, userId);
    }

    private IReadOnlyList<RoleRef> LoadRoles(long appId, long userId)
    {
        var roleIds = Block(_appUserRepo!.GetUserAppRoleIdsAsync(appId, userId)).ToHashSet();
        return Block(_appRoleRepo!.ListDetailsByAppIdAsync(appId))
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => new RoleRef(r.PublicId.ToString(), r.Name))
            .ToList();
    }

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
