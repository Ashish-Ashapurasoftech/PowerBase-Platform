using System.Text.Json;
using PowerBase.Application.Common.Formatting;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Compute-on-read projector for relationship fields. Batched (no N+1):
///   • Lookups  — one parent-row fetch per parent table, then map each child row's value.
///   • Summaries — one GROUP-BY aggregate per summary field, restricted to the page's parent Ids.
/// </summary>
public sealed class RelationalProjector : IRelationalProjector
{
    private static readonly IReadOnlyDictionary<long, object?> EmptyMap = new Dictionary<long, object?>();
    private static readonly JsonSerializerOptions FilterJsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppRepository _appRepo;
    private readonly IUserRepository _userRepo;
    private readonly IQueryContext _queryContext;

    /// <summary>Picked users' long ids by public id, looked up once per projector for every
    /// summary's matching criteria (see ResolveCriteriaValuesAsync).</summary>
    private readonly Dictionary<Guid, long> _userIdsByPublicId = new();

    /// <summary>Each app's display formatting, loaded once per projector (scoped per request) the
    /// first time a Combined Text summary needs it — keyed by AppId, since one scope (e.g. a
    /// pipeline copy) can project tables from more than one app.</summary>
    private readonly Dictionary<long, Domain.ValueObjects.AppFormattingSettings> _appFormatting = new();
    private readonly Dictionary<long, App> _apps = new();

    private readonly IFormulaProjector _formulaProjector;

    /// <summary>How deep summaries over Formula fields are nested right now (a formula can read a
    /// lookup/summary of its own table, which may itself summarize formulas) — capped so a cycle
    /// of relationships can't recurse forever.</summary>
    private int _formulaSummaryDepth;
    private const int MaxFormulaSummaryDepth = 2;

    /// <summary>How many parent tables deep lookups of computed fields are being projected right now.</summary>
    private int _lookupDepth;

    public RelationalProjector(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo,
        IRelationshipRepository relRepo, IAppRepository appRepo, IUserRepository userRepo,
        IFormulaProjector formulaProjector, IQueryContext queryContext)
    {
        _queryContext = queryContext;
        _formulaProjector = formulaProjector;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _relRepo = relRepo;
        _appRepo = appRepo;
        _userRepo = userRepo;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<long, object?>>> ProjectAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default)
    {
        var referenceFields = fields.Where(f => f.TypeCode == "Reference" && f.Fid.HasValue).ToList();
        var lookupFields = fields.Where(f => f.TypeCode == "Lookup" && f.Fid.HasValue).ToList();
        var summaryFields = fields.Where(f => f.TypeCode == "Summary" && f.Fid.HasValue).ToList();
        if ((referenceFields.Count == 0 && lookupFields.Count == 0 && summaryFields.Count == 0) || rows.Count == 0)
            return rows.Select(_ => EmptyMap).ToList();

        var maps = new Dictionary<long, object?>[rows.Count];
        for (var i = 0; i < rows.Count; i++) maps[i] = new Dictionary<long, object?>();

        await ProjectReferencesAndLookupsAsync(table, referenceFields, lookupFields, rows, maps, ct);
        await ProjectSummariesAsync(table, summaryFields, rows, maps, ct);

        return maps;
    }

    private async Task ProjectReferencesAndLookupsAsync(
        AppTable childTable,
        IReadOnlyList<AppField> referenceFields,
        IReadOnlyList<AppField> lookupFields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Dictionary<long, object?>[] maps,
        CancellationToken ct)
    {
        var lookups = lookupFields
            .Select(f => (Field: f, Settings: FormulaTypeMap.ParseLookupSettings(f.Settings)))
            .Where(x => x.Settings is { SourceTableId: not null, ReferenceFid: not null, SourceFid: not null })
            .ToList();

        var refs = referenceFields
            .Select(f => (Field: f, Settings: FormulaTypeMap.ParseReferenceSettings(f.Settings)))
            .Where(x => x.Settings is { ParentTableId: not null })
            .ToList();

        // The child table's relationships — used to pick each reference field's display key
        // (per-relationship DisplayKeyFieldId override → parent table key → Record ID#).
        Dictionary<long, Relationship> relsById = new();
        Dictionary<int, Relationship> relsByRefFid = new();
        if (refs.Count > 0)
        {
            var childRels = await _relRepo.ListByChildTableAsync(childTable.Id, ct);
            relsById = childRels.ToDictionary(r => r.Id);
            relsByRefFid = childRels.GroupBy(r => r.ReferenceFid).ToDictionary(g => g.Key, g => g.First());
        }

        var parentTableIds = lookups.Select(l => l.Settings!.SourceTableId!.Value)
            .Concat(refs.Select(r => r.Settings!.ParentTableId!.Value))
            .Distinct()
            .ToList();

        foreach (var parentTableId in parentTableIds)
        {
            var tableLookups = lookups.Where(l => l.Settings!.SourceTableId!.Value == parentTableId).ToList();
            var tableRefs = refs.Where(r => r.Settings!.ParentTableId!.Value == parentTableId).ToList();
            
            var parentTable = await _tableRepo.GetByIdAsync(parentTableId, ct);
            var parentFields = await _fieldRepo.ListByTableAsync(parentTableId, ct);
            var parentFieldsByFid = parentFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Fid!.Value);

            // Resolve each reference field's display key once (null ⇒ Record ID#, stored row Id shows as-is).
            var displayKeyByRefFid = new Dictionary<int, AppField?>();
            foreach (var (field, settings) in tableRefs)
            {
                var rel = settings!.RelationshipId is long relId && relsById.TryGetValue(relId, out var r)
                    ? r
                    : relsByRefFid.GetValueOrDefault(field.Fid!.Value);
                displayKeyByRefFid[field.Fid!.Value] = KeyFieldResolver.ResolveDisplayKey(rel, parentTable, parentFields);
            }

            // The Fids of the Reference columns on THIS (child) table that point to the parent table.
            var refFids = tableLookups.Select(l => l.Settings!.ReferenceFid!.Value)
                .Concat(tableRefs.Select(r => r.Field.Fid!.Value))
                .Distinct().ToList();

            // The reference column ALWAYS stores the parent's Record ID# (bigint).
            // Fetch parent rows unconditionally by these IDs.
            var resolvedParentId = new Dictionary<(int RowIndex, int RefFid), long?>();
            var parentIds = new HashSet<long>();
            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var refFid in refFids)
                {
                    long? pid = TryGetLong(rows[i], PhysicalNaming.ColumnName(refFid), out var v) ? v : null;
                    resolvedParentId[(i, refFid)] = pid;
                    if (pid is long id) parentIds.Add(id);
                }
            }
            var parentRows = await _recordRepo.GetRowsByIdsAsync(parentTable, parentFields, parentIds, ct);

            // A lookup of a Formula / Lookup / Summary has no column on the parent row, so the parent
            // rows are projected first (relational, then formulas — like a normal record read) and
            // those computed values are what the lookup pulls down. Nested lookups recurse here, capped
            // at LookupChain.MaxLength so a cycle of relationships can't loop forever.
            var parentComputed = await ProjectComputedSourcesAsync(parentTable, parentFields, parentFieldsByFid, tableLookups.Select(l => l.Settings!.SourceFid!.Value), parentRows, ct);

            // Now map the values.
            for (var i = 0; i < rows.Count; i++)
            {
                // 1. Lookups
                foreach (var (field, settings) in tableLookups)
                {
                    object? value = null;
                    if (resolvedParentId.TryGetValue((i, settings!.ReferenceFid!.Value), out var pid) && pid is long parentId
                        && parentRows.TryGetValue(parentId, out var prow))
                    {
                        if (parentFieldsByFid.TryGetValue(settings.SourceFid!.Value, out var srcField)
                            && PhysicalNaming.IsComputedTypeCode(srcField.TypeCode))
                        {
                            if (parentComputed.TryGetValue(parentId, out var computedRow)
                                && computedRow.TryGetValue(settings.SourceFid.Value, out var cv))
                                value = cv;
                        }
                        else
                        {
                            var fidCol = PhysicalNaming.ColumnName(settings.SourceFid.Value);
                            var srcCol = srcField is not null
                                ? (srcField.PhysicalColumnName ?? fidCol)
                                : fidCol;
                            if (!prow.TryGetValue(srcCol, out var v))
                            {
                                prow.TryGetValue(fidCol, out v);
                            }
                            if (v is not null)
                                value = string.IsNullOrWhiteSpace(settings.SourceSubField) ? v : ExtractJsonSubField(v, settings.SourceSubField);
                        }
                    }
                    maps[i][field.Fid!.Value] = value;
                }
                
                // 2. References — surface the display-key value (Id → readable) for each reference
                //    field. When the display key is Record ID# (null) the stored row Id already shows,
                //    so there is nothing to project.
                foreach (var (field, _) in tableRefs)
                {
                    if (displayKeyByRefFid[field.Fid!.Value] is not AppField displayKey) continue;
                    var displayKeyCol = KeyFieldResolver.ColumnName(displayKey);
                    var displayKeyFidCol = displayKey.Fid.HasValue ? PhysicalNaming.ColumnName(displayKey.Fid.Value) : null;
                    if (resolvedParentId.TryGetValue((i, field.Fid!.Value), out var pid) && pid is long parentId
                        && parentRows.TryGetValue(parentId, out var prow))
                    {
                        if (!prow.TryGetValue(displayKeyCol, out var displayKeyValue) && displayKeyFidCol != null)
                        {
                            prow.TryGetValue(displayKeyFidCol, out displayKeyValue);
                        }
                        if (displayKeyValue is not null)
                        {
                            // Store the display-key value string so RecordResult.FromRow can pick it up.
                            maps[i][field.Fid!.Value] = KeyFieldResolver.FormatForSubmit(displayKeyValue);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Computed values (by parent row Id → field Fid) of the parent rows a lookup reads a
    /// Formula / Lookup / Summary from. Empty when no lookup reads a computed field, or the chain is
    /// already <see cref="LookupChain.MaxLength"/> deep (those lookups stay blank).</summary>
    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<long, object?>>> ProjectComputedSourcesAsync(
        AppTable parentTable, IReadOnlyList<AppField> parentFields, IReadOnlyDictionary<int, AppField> parentFieldsByFid,
        IEnumerable<int> sourceFids, IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> parentRows, CancellationToken ct)
    {
        var result = new Dictionary<long, IReadOnlyDictionary<long, object?>>();
        var readsComputed = sourceFids.Any(fid => parentFieldsByFid.TryGetValue(fid, out var f) && PhysicalNaming.IsComputedTypeCode(f.TypeCode));
        if (!readsComputed || parentRows.Count == 0 || _lookupDepth >= LookupChain.MaxLength) return result;

        var ids = parentRows.Keys.ToList();
        var rowList = ids.Select(id => parentRows[id]).ToList();
        _lookupDepth++;
        try
        {
            var seed = await ProjectAsync(parentTable, parentFields, rowList, ct);
            var computed = _formulaProjector.Project(parentFields, rowList, seed, parentTable);
            for (var i = 0; i < ids.Count; i++) result[ids[i]] = computed[i];
        }
        finally { _lookupDepth--; }
        return result;
    }

    private async Task ProjectSummariesAsync(
        AppTable table,
        IReadOnlyList<AppField> summaryFields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Dictionary<long, object?>[] maps,
        CancellationToken ct)
    {
        if (summaryFields.Count == 0) return;

        // This table is the parent — gather its row Ids.
        var rowIds = new long[rows.Count];
        var idSet = new HashSet<long>();
        for (var i = 0; i < rows.Count; i++)
            if (TryGetLong(rows[i], "Id", out var id)) { rowIds[i] = id; idSet.Add(id); }
        if (idSet.Count == 0) return;

        // The per-row value the child's reference column actually stores is ALWAYS the parent's row Id!
        var aggKeys = rowIds.Select(id => (object?)id).ToArray();
        var parentKeyValues = aggKeys.Where(k => k is not null).Select(k => k!).Distinct().ToList();
        if (parentKeyValues.Count == 0) return;

        var app = await GetAppAsync(table.AppId, ct);
        var childFieldsByTable = new Dictionary<long, IReadOnlyList<AppField>>();
        var lookupSourcesByTable = new Dictionary<long, IReadOnlyDictionary<long, AppField>>();

        // This table's own fields, for criteria that compare to "the value in the parent's field".
        // Loaded here rather than taken from the caller, whose list may be only what the viewer's
        // role can see — a summary shows everyone the same value. Loaded at most once, and only
        // when some summary's criteria actually compare to a parent field.
        IReadOnlyList<AppField>? tableFields = null;
        IReadOnlyDictionary<long, AppField> tableFieldsByFid = new Dictionary<long, AppField>();

        foreach (var field in summaryFields)
        {
            var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
            // Canonical casing: rows imported before names were normalized may say "sum".
            var function = SummaryFunctions.Normalize(s?.Function);
            if (s?.ChildTableId is not long childId || s.ReferenceFid is not int refFid || function is null)
            {
                for (var i = 0; i < rows.Count; i++) maps[i][field.Fid!.Value] = null;
                continue;
            }

            if (!childFieldsByTable.TryGetValue(childId, out var childFields))
            {
                childFields = await _fieldRepo.ListByTableAsync(childId, ct);
                childFieldsByTable[childId] = childFields;
            }
            // The parent field each child lookup pulls down — a summary reads a lookup from there.
            if (!lookupSourcesByTable.TryGetValue(childId, out var lookupSources))
            {
                lookupSources = await SummaryLookupSources.LoadAsync(childFields, _fieldRepo, ct);
                lookupSourcesByTable[childId] = lookupSources;
            }
            var filter = ParseFilter(s.FilterTree);
            if (tableFields is null && HasParentFieldCondition(filter))
            {
                tableFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
                tableFieldsByFid = tableFields.Where(f => f.Fid.HasValue)
                    .GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
            }

            // A summary reading an encrypted column can't be aggregated in SQL (ciphertext), so it is
            // computed in memory over decrypted rows. What that path can't do (lookups, calculated
            // targets, parent-field comparisons) is shown blank, never ciphertext.
            var sortFid = function == SummaryFunctions.CombinedText ? s.SortFid : null;
            if (SummaryEncryptionGuard.FindProblem(app, childFields, refFid, s.TargetFid, sortFid, filter, tableFields, lookupSources) is not null)
            {
                for (var i = 0; i < rows.Count; i++) maps[i][field.Fid!.Value] = null;
                continue;
            }
            var inMemory = SummaryEncryptionGuard.ReadsEncrypted(app, childFields, refFid, s.TargetFid, sortFid, filter, tableFields)
                || SummaryEncryptionGuard.ReadsComputed(childFields, sortFid, filter);

            // Criteria comparing to a parent field that has since been deleted or turned into a
            // calculated field would silently drop that condition in SQL and match too many
            // children — shown blank instead, like any other summary saved before today's rules.
            if (HasStaleParentFieldCondition(filter, tableFieldsByFid))
            {
                for (var i = 0; i < rows.Count; i++) maps[i][field.Fid!.Value] = null;
                continue;
            }

            // A summary saved before today's rules (e.g. Sum over a formula field, which has no column
            // and would fail the whole read, or Combined Text over a User field, which would show raw
            // ids) is shown blank rather than computed wrong — same rule as creation.
            if (s.TargetFid is int tFid && childFields.FirstOrDefault(f => f.Fid == tFid) is { } targetField
                && SummaryTargetValidator.FindProblem(function, targetField, s.TargetSubField, lookupSources) is not null)
            {
                for (var i = 0; i < rows.Count; i++) maps[i][field.Fid!.Value] = null;
                continue;
            }

            var childTable = await _tableRepo.GetByIdAsync(childId, ct);
            var childFieldsByFid = childFields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
            // A condition on a looked-up User field resolves its picked user like one on the child's own.
            filter = await ResolveCriteriaValuesAsync(filter, SummaryLookupSources.WithSourceTypes(childFieldsByFid, lookupSources), ct);
            var parentScope = new ParentFieldScope(table.Id, refFid, tableFieldsByFid);
            var formulaTarget = s.TargetFid is int formulaFid && childFieldsByFid.TryGetValue(formulaFid, out var tf)
                && SummaryComputedTargets.IsComputedTarget(tf) ? tf : null;
            var agg = inMemory
                ? await AggregateInMemoryAsync(app, table, tableFields, childTable, childFields, lookupSources, function, refFid, s, sortFid, parentKeyValues, filter, ct)
                : formulaTarget is not null
                ? await AggregateFormulaAsync(childTable, childFields, childFieldsByFid, formulaTarget, function, refFid, s, parentKeyValues, filter, parentScope, ct)
                : function == SummaryFunctions.CombinedText && s.TargetFid is int targetFid
                ? await CombineTextAsync(app, childTable, childFields, childFieldsByFid, lookupSources, refFid, targetFid, s, parentKeyValues, filter, parentScope, ct)
                : await _recordRepo.AggregateByReferenceAsync(childTable, refFid, function, s.TargetFid, parentKeyValues, filter,
                    s.TargetSubField, childFieldsByFid, parentScope, ct);

            // Match results to parents by value, not by boxed type: a reference that was converted
            // from a Number field is a DECIMAL column, so its keys come back as 42.0000m while the
            // parent row Ids here are 42L — and a decimal never equals a long as an object key.
            var aggByKey = new Dictionary<string, object?>();
            foreach (var (key, value) in agg)
                if (NormalizeParentKey(key) is { } k) aggByKey[k] = value;

            var isCount = function is SummaryFunctions.Count or SummaryFunctions.DistinctCount;
            var isExists = function == SummaryFunctions.Exists;
            for (var i = 0; i < rows.Count; i++)
            {
                // No matching children: Count/DistinctCount → 0, Exists → false, others → null.
                if (NormalizeParentKey(aggKeys[i]) is { } key && aggByKey.TryGetValue(key, out var v)) maps[i][field.Fid!.Value] = v;
                else maps[i][field.Fid!.Value] = isCount ? 0 : isExists ? false : null;
            }
        }
    }

    /// <summary>A parent key's canonical text — numbers by value (42L, 42.0000m → "42"), anything
    /// else as its trimmed text — so keys of different CLR types still match.</summary>
    internal static string? NormalizeParentKey(object? key) => key switch
    {
        null => null,
        decimal or double or float or long or int or short or byte =>
            Convert.ToDecimal(key, System.Globalization.CultureInfo.InvariantCulture)
                .ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture)?.Trim(),
    };

    /// <summary>The app (encryption flag + display formatting), loaded once per app per projector
    /// (scoped per request); one scope — e.g. a pipeline copy — can project more than one app.</summary>
    private async Task<App> GetAppAsync(long appId, CancellationToken ct)
    {
        if (!_apps.TryGetValue(appId, out var app))
        {
            app = await _appRepo.GetByIdAsync(appId, ct);
            _apps[appId] = app;
        }
        return app;
    }

    /// <summary>
    /// A summary over a child Formula field: the formula has no column, so the matching children are
    /// fetched, the formula is evaluated on each (with its lookups/summaries as seed, like a normal
    /// record read), and the values aggregated per parent in memory. Returns parentKey → value.
    /// </summary>
    private async Task<IReadOnlyDictionary<object, object?>> AggregateFormulaAsync(
        AppTable childTable, IReadOnlyList<AppField> childFields, IReadOnlyDictionary<long, AppField> childFieldsByFid,
        AppField target, string function, int refFid, SummarySettings s, IReadOnlyCollection<object> parentKeyValues,
        FilterGroup? filter, ParentFieldScope parentScope, CancellationToken ct)
    {
        var rows = await _recordRepo.ListRowsByReferenceAsync(childTable, childFields, refFid, parentKeyValues, filter, childFieldsByFid, parentScope, ct);
        var refCol = PhysicalNaming.ColumnName(refFid);
        return await AggregateFormulaRowsAsync(childTable, childFields, childFieldsByFid, target, function, s, rows,
            row => row.TryGetValue(refCol, out var p) ? p : null, ct);
    }

    /// <summary>The part of a formula summary after the matching children are in hand: evaluate the formula on
    /// each row (lookups/summaries as seed), sort, group by <paramref name="keyOf"/> (the parent a row belongs to,
    /// null = none) and aggregate. Shared by the SQL path and the encrypted in-memory path.</summary>
    private async Task<IReadOnlyDictionary<object, object?>> AggregateFormulaRowsAsync(
        AppTable childTable, IReadOnlyList<AppField> childFields, IReadOnlyDictionary<long, AppField> childFieldsByFid,
        AppField target, string function, SummarySettings s, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        Func<IReadOnlyDictionary<string, object?>, object?> keyOf, CancellationToken ct)
    {
        var result = new Dictionary<object, object?>();
        var resultKind = SummaryComputedTargets.ResultKind(target);
        if (resultKind is null || target.Fid is not int targetFid || rows.Count == 0) return result;

        IReadOnlyList<IReadOnlyDictionary<long, object?>>? seed = null;
        if (_formulaSummaryDepth < MaxFormulaSummaryDepth)
        {
            _formulaSummaryDepth++;
            try { seed = await ProjectAsync(childTable, childFields, rows, ct); }
            finally { _formulaSummaryDepth--; }
        }
        var computed = _formulaProjector.Project(childFields, rows, seed, childTable);

        var options = function == SummaryFunctions.CombinedText ? CombinedTextOptions.From(s) : null;
        var sortCol = options?.SortFid is int sortFid && childFieldsByFid.TryGetValue(sortFid, out var sortField) && !SummaryLookupSources.IsLookup(sortField)
            ? sortField.IsSystem ? SystemColumn(sortFid) : PhysicalNaming.ColumnName(sortFid)
            : null;

        var indexes = Enumerable.Range(0, rows.Count);
        if (sortCol is not null)
        {
            var key = (int i) => rows[i].TryGetValue(sortCol, out var v) ? v : null;
            indexes = options!.SortDescending
                ? indexes.OrderByDescending(key, Comparer<object?>.Default)
                : indexes.OrderBy(key, Comparer<object?>.Default);
        }
        else if (options is { SortDescending: true })
            indexes = indexes.Reverse();   // record order: newest first

        foreach (var group in indexes.Where(i => keyOf(rows[i]) is not null).GroupBy(i => keyOf(rows[i])!))
        {
            var values = group.Select(i => computed[i].GetValueOrDefault(targetFid)).ToList();
            var value = SummaryComputedTargets.Aggregate(function, resultKind, values, options);
            if (value is not null) result[group.Key] = value;
        }
        return result;
    }

    /// <summary>Child rows by table, decrypted, loaded once per projector (scoped per request) so several
    /// summaries over the same child table share one read.</summary>
    private readonly Dictionary<long, IReadOnlyList<IReadOnlyDictionary<string, object?>>> _decryptedChildRows = new();

    /// <summary>A summary over encrypted data: SQL can't match or aggregate ciphertext (the reference
    /// column included), so the child table's rows are decrypted and aggregated here
    /// (<see cref="EncryptedSummaryAggregator"/>). Returns parentKey → value.</summary>
    private async Task<IReadOnlyDictionary<object, object?>> AggregateInMemoryAsync(
        App app, AppTable parentTable, IReadOnlyList<AppField>? parentFields, AppTable childTable,
        IReadOnlyList<AppField> childFields, IReadOnlyDictionary<long, AppField> lookupSources,
        string function, int refFid, SummarySettings s, int? sortFid, IReadOnlyCollection<object> parentKeyValues,
        FilterGroup? filter, CancellationToken ct)
    {
        if (!_decryptedChildRows.TryGetValue(childTable.Id, out var childRows))
        {
            childRows = await _recordRepo.ListAllRowsDecryptedAsync(childTable, childFields, ct);
            _decryptedChildRows[childTable.Id] = childRows;
        }

        var readFids = new List<long>();
        if (s.TargetFid.HasValue) readFids.Add(s.TargetFid.Value);
        if (sortFid.HasValue) readFids.Add(sortFid.Value);
        readFids.AddRange(SummaryEncryptionGuard.ConditionFieldIds(filter));
        var tableFields = childFields;
        (childRows, childFields) = await ResolveLookupValuesAsync(childRows, childFields, lookupSources, readFids, ct);

        // Criteria or a sort that read a Formula/Summary: evaluate it on every child record first (the same projection a
        // record read does), so the value can be compared and sorted like a stored one.
        var conditionFids = SummaryEncryptionGuard.ConditionFieldIds(filter).Concat(sortFid.HasValue ? new long[] { sortFid.Value } : []).ToList();
        if (SummaryEncryptionGuard.ReadsComputed(tableFields, sortFid, filter))
        {
            IReadOnlyList<IReadOnlyDictionary<long, object?>>? seed = null;
            if (_formulaSummaryDepth < MaxFormulaSummaryDepth)
            {
                _formulaSummaryDepth++;
                try { seed = await ProjectAsync(childTable, tableFields, childRows, ct); }
                finally { _formulaSummaryDepth--; }
            }
            var computedValues = _formulaProjector.Project(tableFields, childRows, seed, childTable);
            (childRows, childFields) = EncryptedRowFilter.WithComputedValues(childRows, childFields, computedValues, conditionFids);
        }

        var parentKeys = parentKeyValues.Select(NormalizeParentKey).OfType<string>().ToHashSet();

        // Criteria that compare a child field to a field of its own parent (e.g. "Due is on or after the
        // parent's Start Date"): the parent's value, decrypted, stands in for it - one criteria tree per parent.
        Func<string, FilterGroup?>? filterFor = null;
        if (HasParentFieldCondition(filter) && parentFields is not null)
        {
            var parentRows = await _recordRepo.ListAllRowsDecryptedAsync(parentTable, parentFields, ct);
            var parentById = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
            foreach (var r in parentRows)
                if (r.TryGetValue("Id", out var pid) && EncryptedSummaryAggregator.NormalizeKey(pid) is { } pk) parentById[pk] = r;
            var parentFieldsByFid = parentFields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
            var perParent = new Dictionary<string, FilterGroup?>();
            filterFor = key =>
            {
                if (!perParent.TryGetValue(key, out var resolvedFilter))
                    perParent[key] = resolvedFilter = ResolveParentFieldConditions(filter!, parentById.GetValueOrDefault(key), parentFieldsByFid);
                return resolvedFilter;
            };
        }

        // A Formula (or a child's own Summary) has no column: evaluate it on the matching decrypted rows,
        // exactly as the SQL path does on the rows it fetched, then aggregate per parent.
        if (tableFields.FirstOrDefault(f => f.Fid == s.TargetFid) is { } computedTarget && SummaryComputedTargets.IsComputedTarget(computedTarget))
        {
            var matched = EncryptedSummaryAggregator.MatchingRows(childRows, childFields, refFid, parentKeys, filterFor ?? (_ => filter));
            var keyByRow = new Dictionary<IReadOnlyDictionary<string, object?>, string>(ReferenceEqualityComparer.Instance);
            foreach (var (key, row) in matched) keyByRow[row] = key;
            var fieldsByFid = tableFields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
            return await AggregateFormulaRowsAsync(childTable, tableFields, fieldsByFid, computedTarget, function, s,
                matched.Select(m => m.Row).ToList(), row => keyByRow.GetValueOrDefault(row), ct);
        }

        var options = function == SummaryFunctions.CombinedText ? CombinedTextOptions.From(s) : null;

        Func<object?, string?> format = v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        if (function == SummaryFunctions.CombinedText)
        {
            var target = childFields.FirstOrDefault(f => f.Fid == s.TargetFid);
            var typeCode = string.IsNullOrWhiteSpace(s.TargetSubField) ? target?.TypeCode ?? s.TargetTypeCode ?? "Text" : "Text";
            if (!_appFormatting.TryGetValue(app.Id, out var appFormatting))
            {
                appFormatting = DisplayValueFormatter.ParseAppFormatting(app.Formatting);
                _appFormatting[app.Id] = appFormatting;
            }
            var settings = string.IsNullOrWhiteSpace(s.TargetSubField) ? target?.Settings : null;
            format = v => DisplayValueFormatter.Format(v, typeCode, settings, appFormatting);
        }

        return EncryptedSummaryAggregator.Aggregate(
            childRows, childFields, function, refFid, s.TargetFid, s.TargetSubField, parentKeys, filter, options, format, filterFor);
    }

    /// <summary>The criteria with each "the value in the parent's field" comparison replaced by that parent's
    /// (decrypted) value as a literal. A parent with no value (or no such field) can match nothing, like in SQL.</summary>
    private static FilterGroup ResolveParentFieldConditions(
        FilterGroup group, IReadOnlyDictionary<string, object?>? parentRow, IReadOnlyDictionary<long, AppField> parentFieldsByFid)
    {
        var nodes = new List<FilterNode>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c && ParentFieldScope.IsParentFieldMode(c.ValueMode))
            {
                object? raw = null;
                if (parentRow is not null && c.ValueFieldId is long pf && parentFieldsByFid.TryGetValue(pf, out var parentField))
                    parentRow.TryGetValue(EncryptedRowFilter.Column(parentField), out raw);
                var text = raw switch
                {
                    null or DBNull => null,
                    DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    _ => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture),
                };
                nodes.Add(new FilterNode
                {
                    Condition = string.IsNullOrWhiteSpace(text)
                        ? new FilterCondition { FieldId = 3, Operator = "eq", Value = "-1" }   // Record ID# is never negative
                        : new FilterCondition { FieldId = c.FieldId, Operator = c.Operator, Value = text, SubField = c.SubField, ValueMode = "literal" },
                });
            }
            else if (node.Group is { } sub)
                nodes.Add(new FilterNode { Group = ResolveParentFieldConditions(sub, parentRow, parentFieldsByFid) });
            else
                nodes.Add(node);
        }
        return new FilterGroup { Logic = group.Logic, Nodes = nodes };
    }

    /// <summary>A lookup has no column, so for the lookups the summary reads (target, sort, criteria) its
    /// value is put into a copy of each child row under the lookup's own column: the source table's rows
    /// are decrypted and the value is taken through the child's reference to it. Those lookups are also
    /// replaced, in the returned field list, by a field of their source's type, so they are compared,
    /// summed and formatted as that type. Everything else is returned as it came.</summary>
    private async Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyList<AppField> Fields)> ResolveLookupValuesAsync(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<AppField> childFields,
        IReadOnlyDictionary<long, AppField> lookupSources, IEnumerable<long> readFids, CancellationToken ct)
    {
        var lookups = readFids.Distinct()
            .Select(fid => childFields.FirstOrDefault(f => f.Fid == fid))
            .Where(f => f is not null && SummaryLookupSources.IsLookup(f) && lookupSources.ContainsKey(f.Fid!.Value))
            .Select(f => f!).ToList();

        var resultRows = rows;
        var resultFields = childFields;
        foreach (var lookup in lookups)
        {
            var settings = SummaryLookupSources.Settings(lookup);
            if (settings is not { SourceTableId: long sourceTableId, ReferenceFid: int } || !lookupSources.TryGetValue(lookup.Fid!.Value, out var source))
                continue;

            var sourceTable = await _tableRepo.GetByIdAsync(sourceTableId, ct);
            var sourceFields = await _fieldRepo.ListByTableAsync(sourceTableId, ct);
            var sourceRows = await _recordRepo.ListAllRowsDecryptedAsync(sourceTable, sourceFields, ct);
            (resultRows, resultFields) = EncryptedRowFilter.WithLookupValue(resultRows, resultFields, lookup, settings, source, sourceRows);
        }
        return (resultRows, resultFields);
    }

    /// <summary>A system field's own column (Record ID#, Date Created, …), by its Fid.</summary>
    private static string SystemColumn(int fid) => fid switch
    {
        1 => "CreatedOn", 2 => "ModifiedOn", 4 => "CreatedBy", 5 => "ModifiedBy", _ => "Id",
    };

    /// <summary>
    /// Combined Text: each child value is rendered with its field's display format (the app's
    /// Formatting + the field's own Behavior Settings — "$1,200.00", "09-23-2026", "2 hrs"), blanks
    /// dropped, then joined with the delimiter. Distinct compares the displayed text, so values that
    /// differ only in storage (100 vs 100.0000) still collapse. Returns parentKey → text; parents
    /// with nothing to show are absent (→ null).
    /// </summary>
    private async Task<IReadOnlyDictionary<object, object?>> CombineTextAsync(
        App app, AppTable childTable, IReadOnlyList<AppField> childFields, IReadOnlyDictionary<long, AppField> childFieldsByFid,
        IReadOnlyDictionary<long, AppField> lookupSources, int refFid, int targetFid, SummarySettings s, IReadOnlyCollection<object> parentKeyValues, FilterGroup? filter,
        ParentFieldScope parentScope, CancellationToken ct)
    {
        var options = CombinedTextOptions.From(s);
        var rows = await _recordRepo.ListValuesByReferenceAsync(childTable, refFid, targetFid, s.TargetSubField,
            parentKeyValues, filter, options.SortFid, options.SortDescending, childFieldsByFid, parentScope, ct);
        if (rows.Count == 0) return new Dictionary<object, object?>();

        // An Address sub-key is plain text; otherwise format as the target field currently is — a
        // lookup as the parent field it pulls down (with that field's display settings).
        var target = childFields.FirstOrDefault(f => f.Fid == targetFid);
        if (target is not null && SummaryLookupSources.IsLookup(target))
            target = string.IsNullOrWhiteSpace(SummaryLookupSources.SourceSubField(target))
                ? SummaryLookupSources.ReadableSource(target, lookupSources)
                : null;   // an address part: plain text
        var typeCode = string.IsNullOrWhiteSpace(s.TargetSubField) ? target?.TypeCode ?? s.TargetTypeCode ?? "Text" : "Text";
        if (!_appFormatting.TryGetValue(app.Id, out var appFormatting))
        {
            appFormatting = DisplayValueFormatter.ParseAppFormatting(app.Formatting);
            _appFormatting[app.Id] = appFormatting;
        }

        var result = new Dictionary<object, object?>();
        foreach (var group in rows.GroupBy(r => r.ParentKey))
        {
            var texts = group
                .Select(r => DisplayValueFormatter.Format(r.Value, typeCode, target?.Settings, appFormatting))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!);
            if (options.DistinctValues) texts = texts.Distinct(StringComparer.Ordinal);
            var joined = string.Join(options.Delimiter, texts);
            if (joined.Length > 0) result[group.Key] = joined;
        }
        return result;
    }

    /// <summary>Pulls one JSON property out of a composite Address field's raw stored value
    /// (e.g. just the city). Returns null on any parse failure rather than throwing — malformed
    /// or legacy data shouldn't take down the whole record read.</summary>
    internal static object? ExtractJsonSubField(object? rawValue, string subField)
    {
        if (rawValue is not string json || string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(subField, out var prop) && prop.ValueKind != JsonValueKind.Null
                ? prop.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetLong(IReadOnlyDictionary<string, object?> row, string key, out long value)
    {
        value = 0;
        if (!row.TryGetValue(key, out var raw) || raw is null) return false;
        try { value = Convert.ToInt64(raw); return true; }
        catch { return long.TryParse(raw.ToString(), out value); }
    }

    /// <summary>Turns saved matching criteria into what the SQL compares, exactly as a report run
    /// does: a picked user's public id becomes the long id the column stores, and relative dates
    /// (today, N days ago, "is during the current week") become real dates — worked out on every
    /// read, so "this week" keeps moving. "is the current user" resolves to whoever is reading
    /// the summary (so two users can see different values); with no known user it matches nobody.</summary>
    private async Task<FilterGroup?> ResolveCriteriaValuesAsync(
        FilterGroup? filter, IReadOnlyDictionary<long, AppField> childFieldsByFid, CancellationToken ct)
    {
        if (filter is null) return null;
        var currentUserId = _queryContext.UserId > 0 ? _queryContext.UserId : -1;
        var resolved = await RunReportQueryHandler.ResolveUserFieldValuesAsync(
            filter, childFieldsByFid, currentUserId, _userIdsByPublicId, _userRepo, ct);
        return RunReportQueryHandler.ResolveDateValueModeConditions(resolved);
    }

    private static bool HasParentFieldCondition(FilterGroup? group) =>
        group is not null && group.Nodes.Any(n =>
            (n.Condition is { } c && ParentFieldScope.IsParentFieldMode(c.ValueMode)) || HasParentFieldCondition(n.Group));

    /// <summary>True when a "parentField" condition names a parent field that's gone or has no
    /// column to compare against.</summary>
    internal static bool HasStaleParentFieldCondition(FilterGroup? group, IReadOnlyDictionary<long, AppField> parentFieldsByFid)
    {
        if (group is null) return false;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c && ParentFieldScope.IsParentFieldMode(c.ValueMode)
                && (c.ValueFieldId is not long parentFid
                    || !parentFieldsByFid.TryGetValue(parentFid, out var parentField)
                    || PhysicalNaming.IsComputedTypeCode(parentField.TypeCode)))
                return true;
            if (HasStaleParentFieldCondition(node.Group, parentFieldsByFid)) return true;
        }
        return false;
    }

    private static FilterGroup? ParseFilter(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FilterGroup>(json, FilterJsonOpts); }
        catch (JsonException) { return null; }
    }
}
