using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Queries.RunReport;

public class ReportColumnInfo
{
    public long FieldId { get; init; }
    /// <summary>Unique per-column key for reading this column's value out of a row's Fields
    /// dictionary — always use this, never FieldId, to look up a row's value for this column.
    /// For Table reports this is just FieldId.ToString() (one column per real field, naturally
    /// unique). Summary/Chart reports let a user aggregate the SAME field with several
    /// different functions (e.g. Sum and Avg of Amount) — those columns share FieldId, so Key
    /// disambiguates them (e.g. "agg0_5", "agg1_5").</summary>
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string TypeCode { get; init; } = string.Empty;
}

public class PagedReportRunResult
{
    public IReadOnlyList<RecordResult> Items { get; init; } = [];
    public IReadOnlyList<ReportColumnInfo> Columns { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool IsDataMasked { get; init; }
    /// <summary>Gauge charts only, and only when Chart.GaugeGoalType is "DataValue" — see
    /// ReportRunResponse.ResolvedGaugeGoalValue for the full contract.</summary>
    public decimal? ResolvedGaugeGoalValue { get; init; }
    /// <summary>Table reports only: grand-total aggregates per column (keyed by field id) over EVERY
    /// record matching the report's filters, not just this page. Only columns that opted into a
    /// total/average row appear here; empty when the report hides totals.</summary>
    public IReadOnlyDictionary<string, ColumnTotal> Totals { get; init; } = new Dictionary<string, ColumnTotal>();
}

/// <summary>See <see cref="RunReportQueryHandler.ResolveDeleteScopeAsync"/>.</summary>
public sealed record ReportDeleteScope(
    AppTable Table, IReadOnlyList<AppField> Fields, TableAccessContext Access, FilterGroup? FilterTree, long? RestrictToCreatedBy);

public class RunReportQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IUserRepository _userRepo;
    private readonly IFormulaProjector _formulaProjector;
    private readonly Relationships.IRelationalProjector _relationalProjector;
    private readonly IAzureSearchService _searchService;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IQueryContext _queryContext;
    private readonly ILogger<RunReportQueryHandler> _logger;
    private readonly IAppRepository? _appRepo;

    // GAP #2: Max records returned from Azure AI Search to prevent SQL parameter explosion.
    // Azure AI Search returns GUIDs; injecting >2000 into SQL causes severe performance and
    // parameter-limit issues. Capped here; UI shows a warning when results are truncated.
    private const int AiSearchMaxResults = 2000;

    public RunReportQueryHandler(
        IReportRepository reportRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IUserRepository userRepo,
        IFormulaProjector formulaProjector,
        Relationships.IRelationalProjector relationalProjector,
        IAzureSearchService searchService,
        IAppUserRepository appUserRepo,
        IQueryContext queryContext,
        ILogger<RunReportQueryHandler> logger,
        IAppRepository? appRepo = null)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _userRepo = userRepo;
        _formulaProjector = formulaProjector;
        _relationalProjector = relationalProjector;
        _searchService = searchService;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
        _logger = logger;
        _appRepo = appRepo;
    }

    public async Task<PagedReportRunResult> HandleAsync(RunReportQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var report = await _reportRepo.GetVisibleReportAsync(query.ReportPublicId, ct)
            ?? throw new NotFoundException("Report", query.ReportPublicId);
        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var app = _appRepo != null ? await _appRepo.GetByIdAsync(table.AppId, ct) : null;
        var isAppEncrypted = app?.IsEncrypted ?? false;
        var allFields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var access = await _enforcer.GetTableAccessAsync(table, allFields, ct);

        var appPerms = await _appUserRepo.GetUserAppPermissionsAsync(table.AppId, _queryContext.UserId, ct);
        // Only genuine report designers (create or update capability) qualify for masked builder
        // preview. ReportsRead is a viewer-level permission — those users have no design intent
        // and must be blocked entirely when they lack table data access, not shown masked rows.
        bool isReportBuilder = appPerms.Contains(Domain.Constants.PermissionCodes.ReportsCreate) ||
                               appPerms.Contains(Domain.Constants.PermissionCodes.ReportsUpdate);

        bool isMaskedPreview = !access.CanView && isReportBuilder;

        if (!access.CanView && !isReportBuilder)
            return new PagedReportRunResult { Page = page, PageSize = pageSize };

        if (isMaskedPreview)
        {
            // Report Builder Masked Mode: allow analyzing structure & aggregates without exposing raw records
            access = new TableAccessContext
            {
                Unrestricted = false,
                CanAdd = false,
                CanDelete = false,
                ViewScope = Domain.Constants.RecordScopes.AllRecords,
                ModifyScope = Domain.Constants.RecordScopes.None,
                VisibleFields = allFields,
                EditableFieldIds = new HashSet<long>()
            };
        }

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();
        // Totals/grouping that no longer fit a field whose type was changed are skipped (Change Type).
        PowerBase.Application.Fields.TypeConversion.ReportDefinitionTypeSanitizer.Apply(definition, allFields);

        // Enforce the report's Dynamic Filter configuration server-side: when DynamicFilterType
        // is "Custom", only the fields the report designer picked may be used as a runtime
        // dynamic filter — previously this was a UI-only hint (any filterable field worked
        // regardless of the saved config). Silently drop disallowed params (same "silent
        // intersection" behavior the Columns restriction already uses below) rather than 400ing,
        // since a stale client could otherwise break entirely on a report whose Custom field set
        // was narrowed after the client loaded it.
        var effectiveRuntimeFilters = FilterRuntimeFiltersByDynamicFilterConfig(definition, query.RuntimeFilters);

        // The report's saved filter tree with the runtime (Advanced builder / per-column) filter tree AND'd on top,
        // every "ask the user" answer, User-field and relative-date value already resolved.
        var filterTree = await BuildFilterTreeAsync(definition, query, table, allFields, ct);

        // Table reports: TableSortGroup (Phase 1's unified Sort+Group list) supersedes the legacy
        // single-field GroupByFieldId + SortFields when non-empty — derive both the sort order
        // and the effective group field from it. Reports saved before this existed have an empty
        // TableSortGroup and fall through to the legacy fields below unchanged.
        var tableSortGroupLevel = report.ReportType == "Table"
            ? definition.TableSortGroup.FirstOrDefault(l => l.IsGroup)
            : null;

        // Resolve sort fields — support legacy SortFieldId/SortDesc. A runtime sort (ad-hoc,
        // not persisted — the reference design's header-click sort) replaces the saved sort
        // outright rather than combining with it, matching single-column sort UX.
        IReadOnlyList<SortSpec> sortFields = query.RuntimeSortFieldId.HasValue
            ? [new SortSpec { FieldId = query.RuntimeSortFieldId.Value, Desc = query.RuntimeSortDesc }]
            : (report.ReportType == "Table" && definition.TableSortGroup.Count > 0
                ? definition.TableSortGroup.Select(l => new SortSpec { FieldId = l.FieldId, Desc = l.Desc }).ToList()
                : (definition.SortFields.Count > 0
                    ? definition.SortFields
                    : (definition.SortFieldId.HasValue
                        ? [new SortSpec { FieldId = definition.SortFieldId.Value, Desc = definition.SortDesc }]
                        : [])));

        // Runtime grouping (ad-hoc, not persisted — the per-column kebab menu) overrides both the
        // TableSortGroup-derived group level and the legacy saved GroupByFieldId; ClearGrouping
        // explicitly drops it even if the report has one saved.
        var effectiveGroupByFieldId = query.ClearGrouping ? null : (query.RuntimeGroupByFieldId ?? tableSortGroupLevel?.FieldId ?? definition.GroupByFieldId);
        var effectiveGroupByDesc = query.RuntimeGroupByFieldId.HasValue ? query.RuntimeGroupByDesc : (tableSortGroupLevel?.Desc ?? definition.GroupByDescending);

        // For grouped Table reports: prepend group field as primary sort key so records
        // of the same group are contiguous — the frontend groups the flat result visually.
        if (report.ReportType != "Summary" && effectiveGroupByFieldId.HasValue)
        {
            var gfId = effectiveGroupByFieldId.Value;
            var list = sortFields.ToList();
            if (list.Count == 0 || list[0].FieldId != gfId)
            {
                var without = list.Where(s => s.FieldId != gfId).ToList();
                sortFields = new[] { new SortSpec { FieldId = gfId, Desc = effectiveGroupByDesc } }
                    .Concat(without)
                    .ToArray();
            }
        }

        if (report.ReportType is "Summary" or "Chart")
            return await RunSummaryAsync(table, allFields, access, definition, page, pageSize, query.QuickSearch, effectiveRuntimeFilters, filterTree, isMaskedPreview, ct);

        return await RunTableAsync(table, allFields, access, definition, page, pageSize, filterTree, sortFields,
            effectiveRuntimeFilters, query.QuickSearch, query.QuickSearchFieldIds, query.QuickSearchExact, isMaskedPreview, isAppEncrypted, ct);
    }

    /// <summary>Saved filter tree (legacy flat Filters list supported) with the runtime filter tree AND'd on top; the
    /// "ask the user" answers, User-field and relative-date values are resolved. Role ViewFilter, dynamic and quick-search
    /// filters are merged in by the caller.</summary>
    private async Task<FilterGroup?> BuildFilterTreeAsync(
        ReportDefinition definition, RunReportQuery query, AppTable table, IReadOnlyList<AppField> allFields, CancellationToken ct)
    {
        // Resolve filter tree — support legacy flat Filters list
        var filterTree = definition.FilterTree;
        if (filterTree == null && definition.Filters.Count > 0)
        {
            filterTree = new FilterGroup
            {
                Logic = "and",
                Nodes = definition.Filters.Select(f => new FilterNode
                {
                    Condition = new FilterCondition { FieldId = f.FieldId, Operator = f.Operator, Value = f.Value }
                }).ToList()
            };
        }

        // "<ask the user>" answers are substituted directly into the saved tree's own leaf
        // positions FIRST — before the User/Date resolvers below, so an answer that's itself a
        // picked user's Guid or a relative date tier gets picked up by them exactly like any other
        // literal value. Preserves the saved tree's exact AND/OR structure (see
        // ResolveAskAnswers's doc comment for why this can't be a separate AND'd-on-top group).
        filterTree = ResolveAskAnswers(filterTree, query.AskAnswers, []);

        // "is the current user" AND literal User/MultiUser picker values both need resolving to
        // the plain long core.[User].Id the column actually stores before this tree reaches the
        // SQL builder — see ResolveUserFieldValuesAsync's doc comment for the full reasoning.
        var userFieldLookup = allFields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
        filterTree = await ResolveUserFieldValuesAsync(filterTree, userFieldLookup, _queryContext.UserId, new Dictionary<Guid, long>(), _userRepo, ct, _appUserRepo, table.AppId, _queryContext.UserEmail, _queryContext.UserName);

        // Date fields' relative value-mode tiers (today/yesterday/tomorrow/N days in the past or
        // future) must resolve to an actual date freshly EVERY time the report runs, not once at
        // save time — so this can't be done client-side when the filter is saved. See
        // ResolveDateValueModeConditions's doc comment for the "today" anchor caveat.
        filterTree = ResolveDateValueModeConditions(filterTree);

        // Runtime filter tree (Advanced builder / per-column filters / the "ask the user"
        // prompt's answers — see table-report-view.component.ts's buildRuntimeFilterTree()),
        // AND'd on top of the saved tree — role ViewFilter and dynamic/quick-search filters are
        // merged in further down (RunTableAsync) / below (RunSummaryAsync). Needs the SAME
        // User-field-Guid and Date-relative-tier resolution as the saved tree above, for the same
        // reason: an "ask the user" answer for a User field arrives here, not in `filterTree`.
        //
        // This was reverted once before, when it appeared to "break" a working ask-flow test —
        // that was because the underlying record data itself held the wrong (Guid) value at the
        // time (the write-path bug fixed in RecordWriteService/CreateRecordCommandHandler), so
        // the *unresolved* raw Guid from the picker was accidentally matching the *also-wrong*
        // stored Guid. Now that the write path stores the correct long id, leaving this
        // unresolved is what's actually broken (confirmed: identical saved-vs-ask-prompt filters
        // against the same now-correct data — saved matches, ask doesn't, until this runs).
        if (query.RuntimeFilterTree is { Nodes.Count: > 0 })
        {
            var resolvedRuntimeTree = await ResolveUserFieldValuesAsync(query.RuntimeFilterTree, userFieldLookup, _queryContext.UserId, new Dictionary<Guid, long>(), _userRepo, ct, _appUserRepo, table.AppId, _queryContext.UserEmail, _queryContext.UserName);
            resolvedRuntimeTree = ResolveDateValueModeConditions(resolvedRuntimeTree);
            filterTree = filterTree == null
                ? resolvedRuntimeTree
                : new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = filterTree }, new FilterNode { Group = resolvedRuntimeTree }] };
        }

        return filterTree;
    }

    /// <summary>What a background "Delete these records" job needs to walk the report's matches by Id with plain SQL:
    /// the report's filters plus the role's View and Modify rules as ONE SQL filter tree. Null when the report can't
    /// be answered that way (not a Table report, quick search, a filter on a computed/encrypted field, a role rule on a
    /// computed field) — the caller then falls back to running the report page by page.</summary>
    public async Task<ReportDeleteScope?> ResolveDeleteScopeAsync(RunReportQuery query, CancellationToken ct = default)
    {
        var report = await _reportRepo.GetVisibleReportAsync(query.ReportPublicId, ct)
            ?? throw new NotFoundException("Report", query.ReportPublicId);
        if (report.ReportType != "Table" || !string.IsNullOrWhiteSpace(query.QuickSearch)) return null;

        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var app = _appRepo != null ? await _appRepo.GetByIdAsync(table.AppId, ct) : null;
        var isAppEncrypted = app?.IsEncrypted ?? false;
        var allFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, allFields, ct);
        if (!access.CanView) return null;

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();
        var runtimeFilters = FilterRuntimeFiltersByDynamicFilterConfig(definition, query.RuntimeFilters);
        var tree = await BuildFilterTreeAsync(definition, query, table, allFields, ct);
        tree = MergeRuntimeFilters(tree, allFields, runtimeFilters);

        FilterGroup? And(FilterGroup? left, FilterGroup? right) =>
            left == null ? right
            : right == null ? left
            : new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = left }, new FilterNode { Group = right }] };

        // Same rules RecordModifyGuard enforces on a delete: View rules AND Modify rules.
        tree = And(tree, access.ViewFilter);
        if (!access.Unrestricted) tree = And(tree, access.ModifyFilter);

        var formulaFids = allFields
            .Where(f => (f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings)) ||
                        (f.Fid.HasValue && Relationships.EncryptedRowFilter.IsEncrypted(f, isAppEncrypted)))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();
        if (FormulaFilterSorter.TreeContainsFormulaField(tree, formulaFids)) return null;

        var restrictToCreatedBy = access.RestrictToCreatedBy
            ?? (!access.Unrestricted && access.ModifyScope == Domain.Constants.RecordScopes.OwnRecords ? _queryContext.UserId : null);
        return new ReportDeleteScope(table, allFields, access, tree, restrictToCreatedBy);
    }

    private async Task<PagedReportRunResult> RunTableAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        TableAccessContext access,
        ReportDefinition definition,
        int page, int pageSize,
        FilterGroup? filterTree,
        IReadOnlyList<SortSpec> sortFields,
        IReadOnlyList<(long FieldId, string Value, string? SubField)>? runtimeFilters,
        string? quickSearch,
        IReadOnlyList<long>? quickSearchFieldIds,
        bool quickSearchExact,
        bool isMaskedPreview,
        bool isAppEncrypted,
        CancellationToken ct)
    {
        // Intersect report columns with fields the role can see (drop None-access fields)
        var visibleFieldIds = access.VisibleFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();
        IReadOnlyList<AppField> selectedFields = [];
        if (definition.Columns.Count > 0)
        {
            var fieldMap = allFields
                .Where(f => f.Fid.HasValue)
                .GroupBy(f => (long)f.Fid!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            // A report's own explicit Columns are never filtered by IsReportable here — Custom
            // mode is deliberately allowed to keep or add a field that isn't reportable, and that
            // holds even when this particular report is also the table's default report: what
            // matters is THIS row's own ColumnsMode, not the IsDefault flag. (The reportable
            // restriction only applies to what OTHER reports inherit from this row when they're
            // in "Default columns" mode — see below.)
            selectedFields = definition.Columns
                .Where(id => fieldMap.ContainsKey(id) && visibleFieldIds.Contains(id))
                .Select(id => fieldMap[id])
                .ToList();
        }

        if (selectedFields.Count == 0 && definition.ColumnsMode == "Default")
        {
            var defaultSettings = await _reportRepo.GetDefaultSettingsRecordAsync(table.PublicId, ct);
            if (defaultSettings is not null)
            {
                var defaultColumnIds = (JsonSerializer.Deserialize<ReportDefinition>(defaultSettings.Definition) ?? new ReportDefinition()).Columns;

                if (defaultColumnIds.Count > 0)
                {
                    var fieldMap = allFields
                        .Where(f => f.Fid.HasValue)
                        .GroupBy(f => (long)f.Fid!.Value)
                        .ToDictionary(g => g.Key, g => g.First());
                    selectedFields = defaultColumnIds
                        // Same reasoning as above: the inherited-from list can only ever be reportable
                        // fields, regardless of whatever stale ids the default report's own row still has.
                        .Where(id => fieldMap.ContainsKey(id) && visibleFieldIds.Contains(id) && fieldMap[id].IsReportable)
                        .Select(id => fieldMap[id])
                        .ToList();
                }
                else
                {
                    // The default report exists but has never been explicitly configured — same
                    // "all non-system fields" inference the Default Report Settings columns picker
                    // shows before its first save (table-default-report-settings.component.ts's
                    // initState()). Without this, a report merely inheriting an *unconfigured*
                    // default fell through to the generic "every reportable field" fallback below —
                    // which also pulls in Record ID#/Date Created/etc — instead of matching what the
                    // settings page visually promises until someone actually saves it once.
                    selectedFields = allFields
                        .Where(f => f.Fid.HasValue && f.IsReportable && !f.IsSystem && visibleFieldIds.Contains((long)f.Fid!.Value))
                        .ToList();
                }
            }
        }

        if (selectedFields.Count == 0)
        {
            selectedFields = allFields.Where(f => f.Fid.HasValue && f.IsReportable && visibleFieldIds.Contains((long)f.Fid!.Value)).ToList();
        }
        if (selectedFields.Count == 0)
        {
            selectedFields = allFields.Where(f => f.Fid.HasValue && !f.IsHidden && visibleFieldIds.Contains((long)f.Fid!.Value)).ToList();
        }

        // Merge role record filter into the report's filter tree.
        // NOTE: ViewFilter is intentionally merged BEFORE runtime filters but we must track it
        // separately so the AI Search path (OData filter below) only receives user-applied filters —
        // not role-enforcement conditions. Role filters always go through SQL to guarantee correctness
        // regardless of AI Search index freshness.
        // We keep the merged filterTree for OData path (user filters only) and merge ViewFilter into
        // the final SQL tree after AI Search resolves its ID set.
        var userFilterTree = filterTree; // filterTree at this point = report save + runtime, no ViewFilter yet

        // Merge runtime filters (dynamic/quick-search) into the user filter tree
        userFilterTree = MergeRuntimeFilters(userFilterTree, allFields, runtimeFilters);

        var columns = selectedFields.Select(f => new ReportColumnInfo
        {
            FieldId = f.Fid.HasValue ? (long)f.Fid.Value : f.Id,
            Key = (f.Fid.HasValue ? (long)f.Fid.Value : f.Id).ToString(),
            Name = string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label,
            TypeCode = f.TypeCode,
        }).ToList();

        // Determine which field IDs are formula (compute-on-read, no physical column)
        // OR are encrypted, so we must filter/sort them in memory instead of SQL.
        var formulaFids = allFields
            .Where(f => (f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings)) || 
                        (f.Fid.HasValue && Relationships.EncryptedRowFilter.IsEncrypted(f, isAppEncrypted)))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();

        // Computed fields only (no encrypted ones): they are not stored, so the AI Search index cannot filter on them.
        var computedFids = allFields
            .Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();

        var hasFormulaFilters = FormulaFilterSorter.TreeContainsFormulaField(userFilterTree, formulaFids);
        var hasFormulaSorts   = sortFields.Any(s => formulaFids.Contains(s.FieldId));

        var searchOrderBy = BuildSearchOrderBy(sortFields, allFields);
        var canSortInSearch = sortFields.Count == 0 || searchOrderBy != null;
        var canExpressUserFilter = userFilterTree == null || OData.ODataFilterBuilder.CanExpress(userFilterTree, allFields);
        var hasSearchableFields = allFields.Any(f => f.IsSearchable || f.IsFilterable);

        // SOLUTION 1: Fast Azure AI Search paged execution for large tables.
        // If grid search is enabled and sorting or filtering involves encrypted fields (which are indexed as plaintext in AI Search)
        // or user filters / quick search, execute the paging and sorting directly in Azure AI Search.
        // Merge ViewFilter into effective filter for AI Search verification
        FilterGroup? effectiveFilterTree = userFilterTree;
        if (access.ViewFilter != null)
        {
            effectiveFilterTree = effectiveFilterTree == null
                ? access.ViewFilter
                : new FilterGroup
                {
                    Logic = "and",
                    Nodes = [new FilterNode { Group = effectiveFilterTree }, new FilterNode { Group = access.ViewFilter }]
                };
        }

        var canExpressEffectiveFilter = effectiveFilterTree == null || OData.ODataFilterBuilder.CanExpress(effectiveFilterTree, allFields);

        // SOLUTION 1: Fast Azure AI Search paged execution for large tables.
        // If grid search is enabled and sorting or filtering involves encrypted fields (which are indexed as plaintext in AI Search)
        // or user filters / quick search, execute the paging and sorting directly in Azure AI Search.
        // Security requirement: Role ViewFilter must be fully expressible in AI Search, and RestrictToCreatedBy must not be present
        // (CreatedBy is not indexed in AI Search; queries with RestrictToCreatedBy execute via SQL).
        var shouldTryFastAiPaging = _searchService.IsGridSearchEnabled
            && hasSearchableFields
            && canExpressEffectiveFilter
            && canSortInSearch
            && !access.RestrictToCreatedBy.HasValue
            && !FormulaFilterSorter.TreeContainsFormulaField(effectiveFilterTree, computedFids)
            && (effectiveFilterTree != null || !string.IsNullOrWhiteSpace(quickSearch) || hasFormulaSorts || hasFormulaFilters || isAppEncrypted);

        // Issue 2: Newly created rows indexing latency check.
        // If there were records inserted/updated in the last 15 seconds in the database for this table,
        // search indexing may still be catching up. Fall back to standard SQL pipeline to guarantee
        // newly created child rows are immediately visible.
        if (shouldTryFastAiPaging)
        {
            try
            {
                var recentChanged = await _recordRepo.ListRecordsChangedSinceAsync(
                    table, DateTime.UtcNow.AddSeconds(-15), 0, long.MaxValue, 1, ct);
                if (recentChanged.Count > 0)
                {
                    shouldTryFastAiPaging = false;
                }
            }
            catch
            {
                // Proceed if recent check is unavailable
            }
        }

        if (shouldTryFastAiPaging && await _searchService.IsHealthyAsync(ct))
        {
            try
            {
                var odata = effectiveFilterTree != null ? OData.ODataFilterBuilder.Build(effectiveFilterTree, allFields) : null;
                var targetIndex = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(allFields)
                    ? PowerBase.Application.Search.SearchSlotResolver.GetTableIndexName(table.Id)
                    : null;

                var (pagedIds, totalMatches) = await _searchService.SearchRecordsPagedAsync(
                    _queryContext.TenantId, table.Id, quickSearch, odata, page, pageSize, searchOrderBy, targetIndex, ct);

                if (totalMatches == 0 || pagedIds.Count == 0)
                {
                    return new PagedReportRunResult { Page = page, PageSize = pageSize, Columns = columns, TotalCount = 0 };
                }

                // Fetch ONLY the requested page rows from SQL (e.g. 25 rows)
                var pagedRows = await _recordRepo.ListByPublicIdsAsync(table, allFields, pagedIds, ct);

                // If role ViewFilter or RestrictToCreatedBy applies, verify candidate page (defense in depth)
                if (access.RestrictToCreatedBy.HasValue)
                {
                    pagedRows = pagedRows.Where(r => r.TryGetValue("CreatedBy", out var cb) && cb != null && Convert.ToInt64(cb, CultureInfo.InvariantCulture) == access.RestrictToCreatedBy.Value).ToList();
                }
                if (access.ViewFilter != null)
                {
                    var valuesLookup = pagedRows.Select(r => (Row: r, Map: ExtractFieldValuesByFid(r, allFields))).ToList();
                    pagedRows = valuesLookup.Where(v => PipelineFilterEvaluator.EvaluateFilterGroup(access.ViewFilter, v.Map, allFields)).Select(v => v.Row).ToList();
                }

                if (pagedRows.Count == 0 && totalMatches > 0 && access.ViewFilter != null)
                {
                    throw new InvalidOperationException("Fast AI Search returned candidates that failed post-retrieval role authorization; falling back to SQL.");
                }

                var relational = Relationships.ReferenceOriginalFallback.Apply(allFields, pagedRows,
                    await _relationalProjector.ProjectAsync(table, allFields, pagedRows, ct));
                var computed = _formulaProjector.Project(allFields, pagedRows, relational, table);
                var userNames = await ResolveUserNamesAsync(pagedRows, allFields, _userRepo, ct, computed);
                var itemsFast = pagedRows.Select((row, i) => RecordResult.FromRow(row, selectedFields, userNames, computed[i])).ToList();
                var totalFast = (int)Math.Min(totalMatches, int.MaxValue);
                if (page == 1 && pagedRows.Count < pageSize && totalFast > pagedRows.Count)
                {
                    totalFast = pagedRows.Count;
                }

                var totalsFast = new Dictionary<string, ColumnTotal>();
                var totalColsFast = definition.HideTotals || isMaskedPreview
                    ? new List<AppField>()
                    : selectedFields.Where(WantsTotals).ToList();
                if (totalColsFast.Count > 0 && itemsFast.Count > 0)
                {
                    totalsFast = AggregateInMemory(itemsFast, totalColsFast);
                }

                return new PagedReportRunResult
                {
                    Items = itemsFast,
                    Columns = columns,
                    TotalCount = totalFast,
                    Page = page,
                    PageSize = pageSize,
                    IsDataMasked = isMaskedPreview,
                    Totals = totalsFast,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[RunReport] Fast AI Search paged execution failed for table {TableId}. Falling back to standard pipeline.", table.Id);
            }
        }

        // Apply Quick Search across searchable text fields (OR) — restricted to
        // quickSearchFieldIds when given (a caller-specified subset, e.g. a dashboard Search
        // widget scoped to "Selected fields"), otherwise every IsSearchable text-ish field.
        if (!string.IsNullOrWhiteSpace(quickSearch))
        {
            var hasSearchable = allFields.Any(f => f.IsSearchable);

            var useAiSearch = _searchService.IsGridSearchEnabled && hasSearchable && await _searchService.IsHealthyAsync(ct);
            var aiSearchSucceeded = false;

            if (useAiSearch)
            {
                try
                {
                    var targetIndex = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(allFields)
                        ? PowerBase.Application.Search.SearchSlotResolver.GetTableIndexName(table.Id)
                        : null;
                    var aiMatches = targetIndex != null
                        ? await _searchService.SearchRecordsAsync(_queryContext.TenantId, table.Id, quickSearch, targetIndex, ct)
                        : await _searchService.SearchRecordsAsync(_queryContext.TenantId, table.Id, quickSearch, ct);
                    if (aiMatches.Count > 0)
                    {
                        var cappedMatches = aiMatches.Count > AiSearchMaxResults
                            ? aiMatches.Take(AiSearchMaxResults).ToList()
                            : aiMatches;

                        // GAP #2: Use direct chunked IN query instead of OR FilterGroup nodes.
                        userFilterTree = await BuildAiIdFilterAsync(table, cappedMatches, userFilterTree, ct);
                        aiSearchSucceeded = true;
                    }
                    else
                    {
                        // Quick search matched nothing.
                        return new PagedReportRunResult { Page = page, PageSize = pageSize, Columns = columns };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[QuickSearch] Azure AI Search unavailable for table {TableId}. Falling back to SQL LIKE.", table.Id);
                }
            }


            if (!aiSearchSucceeded)
            {
                // Standard SQL LIKE fallback (used when AI Search is disabled, unhealthy, or returns 0 matches).
                var textFields = allFields
                    .Where(f => !f.IsHidden && !PhysicalNaming.IsComputedTypeCode(f.TypeCode) &&
                                !f.TypeCode.Equals("File", StringComparison.OrdinalIgnoreCase) &&
                                !f.TypeCode.Equals("Attachment", StringComparison.OrdinalIgnoreCase) &&
                                !f.TypeCode.Equals("Signature", StringComparison.OrdinalIgnoreCase) &&
                                !f.TypeCode.Equals("DateRange", StringComparison.OrdinalIgnoreCase) &&
                                !f.TypeCode.Equals("NumericRange", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (quickSearchFieldIds is { Count: > 0 })
                {
                    var allowed = quickSearchFieldIds.ToHashSet();
                    textFields = textFields.Where(f => allowed.Contains(f.Fid.HasValue ? (long)f.Fid.Value : f.Id)).ToList();
                }
                if (textFields.Count > 0)
                {
                    var searchOperator = quickSearchExact ? "eq" : "contains";
                    var qsNodes = textFields.Select(f => new FilterNode
                    {
                        Condition = new FilterCondition { FieldId = f.Fid.HasValue ? (long)f.Fid.Value : f.Id, Operator = searchOperator, Value = quickSearch }
                    }).ToList();
                    var qsGroup = new FilterGroup { Logic = "or", Nodes = qsNodes };
                    userFilterTree = userFilterTree == null
                        ? qsGroup
                        : new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = userFilterTree }, new FilterNode { Group = qsGroup }] };
                }
            }
        }

        // Fallback: If Fast AI Search could not be used (e.g. sorting by an in-memory formula),
        // but user filter can be expressed in AI search, pre-filter IDs (capped at AiSearchMaxResults)
        // so that SQL only pulls matching rows.
        // A computed field (Formula / Lookup / Summary) has no stored value, so the search index holds nothing for
        // it: routing a condition on one through AI Search matches no record at all. Such a tree runs through the
        // SQL + in-memory path below instead, which evaluates the formula.
        if (_searchService.IsGridSearchEnabled && userFilterTree != null && hasSearchableFields
            && OData.ODataFilterBuilder.CanExpress(userFilterTree, allFields)
            && !FormulaFilterSorter.TreeContainsFormulaField(userFilterTree, computedFids))
        {
            var odata = OData.ODataFilterBuilder.Build(userFilterTree, allFields);
            if (!string.IsNullOrWhiteSpace(odata))
            {
                try
                {
                    // If wide table (> 150 search fields), query its dedicated index directly
                    var targetIndex = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(allFields)
                        ? $"pb-tbl-{table.Id}"
                        : null;
                    var aiMatches = targetIndex != null
                        ? await _searchService.SearchRecordsByFilterAsync(_queryContext.TenantId, table.Id, odata, targetIndex, ct)
                        : await _searchService.SearchRecordsByFilterAsync(_queryContext.TenantId, table.Id, odata, ct);
                    var cappedMatches = aiMatches.Count > AiSearchMaxResults
                        ? aiMatches.Take(AiSearchMaxResults).ToList()
                        : aiMatches;

                    if (cappedMatches.Count == 0)
                    {
                        return new PagedReportRunResult { Page = page, PageSize = pageSize, Columns = columns };
                    }

                    userFilterTree = await BuildAiIdFilterAsync(table, cappedMatches, null, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[ODataFilter] Azure AI Search unavailable for table {TableId}. Executing filter via SQL.", table.Id);
                }
            }
        }

        // NOW merge role ViewFilter into the final SQL filter tree — always via SQL, never AI Search.
        filterTree = userFilterTree;
        if (access.ViewFilter != null)
        {
            filterTree = filterTree == null
                ? access.ViewFilter
                : new FilterGroup
                {
                    Logic = "and",
                    Nodes = [new FilterNode { Group = filterTree }, new FilterNode { Group = access.ViewFilter }]
                };
        }

        hasFormulaFilters = FormulaFilterSorter.TreeContainsFormulaField(filterTree, formulaFids);
        hasFormulaSorts   = sortFields.Any(s => formulaFids.Contains(s.FieldId));

        IReadOnlyList<RecordResult> items;
        int total;
        var totals = new Dictionary<string, ColumnTotal>();
        // Columns that opted into a Total/Average row — aggregated over ALL matching records below.
        var totalCols = definition.HideTotals || isMaskedPreview
            ? new List<AppField>()
            : selectedFields.Where(WantsTotals).ToList();

        if (hasFormulaFilters || hasFormulaSorts)
        {
            // Strip formula conditions from the SQL filter tree; keep them for in-memory pass.
            var (physicalFilterTree, formulaConditions) = FormulaFilterSorter.SplitFilterTree(filterTree, formulaFids);

            // When sorts include formula fields, skip SQL ORDER BY (we'll re-sort everything in-memory).
            IReadOnlyList<SortSpec> sqlSorts = hasFormulaSorts ? [] : sortFields;

            var allRows = await _recordRepo.ListAllMatchingAsync(table, allFields,
                physicalFilterTree, sqlSorts,
                restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

            var allRelational = Relationships.ReferenceOriginalFallback.Apply(allFields, allRows,
                await _relationalProjector.ProjectAsync(table, allFields, allRows, ct));
            var allComputed = _formulaProjector.Project(allFields, allRows, allRelational, table);
            var pairs = allRows.Zip(allComputed, (r, c) => (Row: r, Computed: c)).ToList();

            // Apply formula-field conditions in memory.
            if (formulaConditions != null)
                pairs = FormulaFilterSorter.ApplyFormulaFilters(pairs, formulaConditions, allFields);

            // If any sort key is a formula field, re-sort all rows in memory (covers all sort keys).
            if (hasFormulaSorts)
                pairs = FormulaFilterSorter.ApplySort(pairs, sortFields, allFields);

            total = pairs.Count;

            // Every matching row is already in memory here, so aggregate them directly.
            if (totalCols.Count > 0)
                totals = AggregateInMemory(pairs.Select(p => RecordResult.FromRow(p.Row, selectedFields, null, p.Computed)).ToList(), totalCols);

            // Paginate in memory.
            var pagePairs = pairs.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var userNames = await ResolveUserNamesAsync(pagePairs.Select(p => p.Row), allFields, _userRepo, ct, pagePairs.Select(p => p.Computed));
            items = pagePairs.Select(p => RecordResult.FromRow(p.Row, selectedFields, userNames, p.Computed)).ToList();
        }
        else
        {
            // Normal path — SQL handles pagination, sorting, and filtering entirely.
            var rows = await _recordRepo.ListAsync(table, allFields, page, pageSize, filterTree, sortFields,
                restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
            total = await _recordRepo.CountAsync(table, allFields, filterTree,
                restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

            var relational = Relationships.ReferenceOriginalFallback.Apply(allFields, rows,
                await _relationalProjector.ProjectAsync(table, allFields, rows, ct));
            var computed = _formulaProjector.Project(allFields, rows, relational, table);
            var userNames = await ResolveUserNamesAsync(rows, allFields, _userRepo, ct, computed);
            items = rows.Select((row, i) => RecordResult.FromRow(row, selectedFields, userNames, computed[i])).ToList();

            if (totalCols.Count > 0)
            {
                // Plain stored columns: one SQL aggregate with the same WHERE as the page query.
                var physicalCols = totalCols.Where(c => !PhysicalNaming.IsComputedTypeCode(c.TypeCode) && !c.IsEncrypted).ToList();
                if (physicalCols.Count > 0)
                {
                    foreach (var kv in await _recordRepo.AggregateColumnsAsync(table, allFields, physicalCols, filterTree,
                                 restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct))
                        totals[kv.Key.ToString()] = kv.Value;
                }

                // Formula / encrypted columns have no SQL-aggregatable value, so evaluate them over
                // every matching record in memory (capped at 10,000 for unfiltered 10M-row tables to prevent OOM).
                var memoryCols = totalCols.Except(physicalCols).ToList();
                if (memoryCols.Count > 0)
                {
                    var everyRow = await _recordRepo.ListAllMatchingAsync(table, allFields, filterTree, [], restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

                    var everyRelational = await _relationalProjector.ProjectAsync(table, allFields, everyRow, ct);
                    var everyComputed = _formulaProjector.Project(allFields, everyRow, everyRelational, table);
                    var everyResult = everyRow.Select((r, i) => RecordResult.FromRow(r, selectedFields, null, everyComputed[i])).ToList();
                    foreach (var kv in AggregateInMemory(everyResult, memoryCols))
                        totals[kv.Key] = kv.Value;
                }
            }
        }

        if (isMaskedPreview)
        {
            // Table-type masked preview: expose ONLY row count + column structure.
            // Requirement [UPDATED]: "preview shows row counts and aggregate outputs only —
            // never raw record data." Individual rows are suppressed entirely — even replacing
            // values with placeholder dots would still reveal row count per page and data shape.
            // Summary/Chart reports already return only aggregates via RunSummaryAsync.
            items = [];
        }

        // columns variable is now defined at the top of RunTableAsync
        return new PagedReportRunResult
        {
            Items = items,
            Columns = columns,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
            IsDataMasked = isMaskedPreview,
            Totals = totals,
        };
    }

    /// <summary>True when this column opted into a Total/Average row (numeric family, Duration and
    /// their formula variants) or a checked-count row (checkbox) — the same opt-ins the grid reads.</summary>
    private static bool WantsTotals(AppField f)
    {
        var isBool = f.TypeCode is "Boolean" or "Formula_Bool";
        var isNumeric = f.TypeCode is "Number" or "Currency" or "Percent" or "Rating" or "Duration"
            or "Formula_Number" or "Formula_Duration";
        if ((!isBool && !isNumeric) || string.IsNullOrWhiteSpace(f.Settings)) return false;
        try
        {
            using var doc = JsonDocument.Parse(f.Settings);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.True) continue;
                if (isBool
                        ? p.Name.Equals("showTotalsRow", StringComparison.OrdinalIgnoreCase)
                        : p.Name.Equals("showTotalInReports", StringComparison.OrdinalIgnoreCase)
                          || p.Name.Equals("showAverageInReports", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>Same aggregates as <see cref="IRecordRepository.AggregateColumnsAsync"/>, computed over
    /// already-materialised rows — for formula / encrypted columns that SQL can't sum. Mirrors the
    /// grid's old per-page maths: a non-numeric value counts as 0, a checkbox counts when checked.</summary>
    private static Dictionary<string, ColumnTotal> AggregateInMemory(IReadOnlyList<RecordResult> rows, IReadOnlyList<AppField> columns)
    {
        var result = new Dictionary<string, ColumnTotal>();
        foreach (var col in columns)
        {
            var key = (col.Fid ?? col.Id).ToString();
            var isBool = col.TypeCode is "Boolean" or "Formula_Bool";
            decimal sum = 0;
            long nonBlank = 0;
            foreach (var row in rows)
            {
                if (!row.Fields.TryGetValue(key, out var raw) || raw is null || (raw is string s && s.Length == 0)) continue;
                nonBlank++;
                if (isBool)
                {
                    var truthy = raw is bool b ? b
                        : raw is string str ? str.Equals("true", StringComparison.OrdinalIgnoreCase) || str == "1" || str.Equals("yes", StringComparison.OrdinalIgnoreCase)
                        : decimal.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var bn) && bn == 1;
                    if (truthy) sum += 1;
                }
                else if (decimal.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture),
                             System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n))
                    sum += n;
            }
            result[key] = new ColumnTotal { Sum = sum, NonBlankCount = nonBlank, RowCount = rows.Count };
        }
        return result;
    }

    private async Task<FilterGroup?> BuildAiIdFilterAsync(AppTable table, IReadOnlyList<Guid> aiMatches, FilterGroup? existingFilter, CancellationToken ct)
    {
        var matchedIds = await _recordRepo.GetIdsByPublicIdsAsync(table, aiMatches, ct);
        if (matchedIds.Count == 0)
        {
            // No matching records found in the database. Return a filter guaranteed to match nothing.
            var emptyGroup = new FilterGroup
            {
                Logic = "and",
                Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "eq", Value = "-1" } }]
            };
            return existingFilter == null
                ? emptyGroup
                : new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = existingFilter }, new FilterNode { Group = emptyGroup }] };
        }

        // GAP #2: Use "in" operator with a serialized array instead of generating N "eq" OR conditions.
        // RecordRepository's BuildConditionClause parses this array into a single SQL IN (@p1, @p2, ...) clause.
        // Since we capped matches at 2000, we stay safely under SQL Server's 2100 parameter limit.
        var idJson = JsonSerializer.Serialize(matchedIds);
        var aiSearchGroup = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "in", Value = idJson } }]
        };

        return existingFilter == null
            ? aiSearchGroup
            : new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = existingFilter }, new FilterNode { Group = aiSearchGroup }] };
    }

    private static List<string>? BuildSearchOrderBy(IReadOnlyList<SortSpec>? sortFields, IReadOnlyList<AppField> allFields)
    {
        if (sortFields == null || sortFields.Count == 0) return null;
        var isWide = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(allFields);
        var list = new List<string>();
        foreach (var s in sortFields)
        {
            var f = allFields.FirstOrDefault(x => (x.Fid.HasValue && x.Fid.Value == s.FieldId) || x.Id == s.FieldId);
            if (f == null) return null; // unknown field -> cannot order in search
            if (PhysicalNaming.IsComputedTypeCode(f.TypeCode)) return null; // dynamic formula not in search slots

            var slot = PowerBase.Application.Search.SearchSlotResolver.ResolveSlotName(f, allFields);
            if (!isWide && slot.StartsWith("f_")) return null; // Not present in shared typed index

            var dir = s.Desc ? "desc" : "asc";
            list.Add($"{slot} {dir}");
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>When definition.DynamicFilterType is "Custom", restricts runtimeFilters to only
    /// the FieldId (+ matching SubField, for Address sub-fields) combinations present in the
    /// report's CustomDynamicFilterItems/CustomDynamicFilterFields — everything else is silently
    /// dropped. For "Default"/"None" (or unrecognized) DynamicFilterType, runtimeFilters passes
    /// through unchanged (this call site does not restrict "Default" mode's own field set, which
    /// today is defined by the table's Default Report Settings, resolved elsewhere).</summary>
    internal static IReadOnlyList<(long FieldId, string Value, string? SubField)>? FilterRuntimeFiltersByDynamicFilterConfig(
        ReportDefinition definition,
        IReadOnlyList<(long FieldId, string Value, string? SubField)>? runtimeFilters)
    {
        if (runtimeFilters is not { Count: > 0 })
            return runtimeFilters;

        if (!string.Equals(definition.DynamicFilterType, "Custom", StringComparison.OrdinalIgnoreCase))
            return runtimeFilters;

        var allowedFieldIds = definition.CustomDynamicFilterFields.ToHashSet();
        // (FieldId, SubField) pairs — SubField normalized to "" so a plain-field allowance
        // (SubField == null) also matches a runtime filter whose SubField happens to be "".
        var allowedFieldSubFieldPairs = definition.CustomDynamicFilterItems
            .Select(i => (i.FieldId, SubField: i.SubField ?? string.Empty))
            .ToHashSet();

        return runtimeFilters
            .Where(rf =>
                allowedFieldIds.Contains(rf.FieldId) ||
                allowedFieldSubFieldPairs.Contains((rf.FieldId, rf.SubField ?? string.Empty)))
            .ToList();
    }

    /// <summary>Recursively resolves two kinds of User-field filter values that can't be
    /// evaluated as literal SQL text as-is:
    /// (1) "isCurrentUser" conditions rewrite to a literal "eq" against the caller's own user id.
    /// (2) Literal User/MultiUser condition VALUES arrive from the frontend's user picker as
    ///     userPublicId Guid string(s) (comma-joined for MultiUser) — AppUserPickerResponse's own
    ///     wire shape — but the column itself stores the plain long core.[User].Id (confirmed
    ///     against every other User-field read path — see ResolveUserNamesAsync below,
    ///     RecordResult.ResolveUserValue, GetDistinctFieldValuesQueryHandler, and
    ///     RecordRepository's filter-SQL builder, which compares the stored column verbatim with
    ///     no Guid conversion). Left unresolved, a picked user would never match. Each Guid part
    ///     is resolved via IUserRepository.GetByPublicIdAsync and cached per call so the same
    ///     picked user referenced by several conditions only costs one lookup; a part that isn't
    ///     a parseable Guid (already numeric, or free text) passes through unchanged.
    /// Do not copy RolePermissionEnforcer's appUser.UserPublicId.ToString() pattern for (1) — that
    /// path is unproven (mock-only unit test, its UI is disabled) and writes the wrong id space.
    /// Returns a new tree (never mutates the input) so callers holding onto the original
    /// `definition.FilterTree` reference elsewhere aren't affected.</summary>
    /// <summary>Substitutes each "&lt;ask the user&gt;" condition's value with the caller-supplied
    /// answer, keyed by the condition's tree-path (root-to-leaf node indices joined with "-" —
    /// must match table-report-view.component.ts's askConditionKey exactly). Preserves the tree's
    /// exact AND/OR structure — this is why ask-answers are resolved this way instead of merged
    /// as a separate flat group AND'd on top of everything else: a flat AND merge can't correctly
    /// represent an ask-condition that lives inside an OR group (confirmed by direct testing — it
    /// silently narrowed a saved "A or B" filter into "A and B"). An unanswered ask-condition (no
    /// matching key, or a blank answer — e.g. the report's saved definition was run directly,
    /// bypassing the prompt entirely) is left with ValueMode "ask" and an empty Value, which
    /// BuildConditionClause's existing empty-value guard already treats as a no-op.</summary>
    internal static FilterGroup? ResolveAskAnswers(FilterGroup? group, IReadOnlyDictionary<string, string>? answers, List<int> path)
    {
        if (group is null) return null;
        var nodes = new List<FilterNode>();
        for (var i = 0; i < group.Nodes.Count; i++)
        {
            var n = group.Nodes[i];
            var childPath = new List<int>(path) { i };
            FilterCondition? newCondition = n.Condition;
            if (n.Condition is { } cond && string.Equals(cond.ValueMode, "ask", StringComparison.OrdinalIgnoreCase))
            {
                var key = string.Join("-", childPath);
                if (answers != null && answers.TryGetValue(key, out var answer) && !string.IsNullOrEmpty(answer))
                {
                    newCondition = new FilterCondition
                    {
                        FieldId = cond.FieldId, Operator = cond.Operator, SubField = cond.SubField,
                        Value = answer, ValueMode = "literal", ValueFieldId = null,
                    };
                }
            }
            nodes.Add(new FilterNode
            {
                Condition = newCondition,
                Group = ResolveAskAnswers(n.Group, answers, childPath),
            });
        }
        return new FilterGroup { Logic = group.Logic, Nodes = nodes };
    }

    /// <summary>Resolves the Date-group's relative value-mode tiers (today/yesterday/tomorrow/
    /// pastDays/futureDays — the "day(s) in the past/future" tiers carry the day count in
    /// cond.Value) into a literal date string, matching the same "resolve one layer above the SQL
    /// builder" shape as the other resolvers in this file. Anchored to UTC "now" — a known
    /// simplification, since no per-tenant/app timezone is tracked anywhere else in this codebase
    /// either. Non-Date-group conditions and any other ValueMode pass through unchanged.
    ///
    /// during/notDuring conditions (duringCurrent/duringPrevious/duringNext, "count:unit" in
    /// Value — see the frontend's filter-condition-operators.ts) are a special case: unlike every
    /// other tier here, they don't resolve to a single literal — they resolve to a RANGE, so the
    /// condition NODE itself gets replaced with a small {gte,lte}/{lt,gt} GROUP instead (see
    /// ResolveDuringCondition). This is transparent to every downstream consumer of the resolved
    /// tree (the SQL builder, ODataFilterBuilder) since by the time they see it, "during"/
    /// "notDuring" no longer exist — only the gt/gte/lt/lte operators they already support.</summary>
    public static FilterGroup? ResolveDateValueModeConditions(FilterGroup? group)
    {
        if (group is null) return null;
        var today = DateTime.UtcNow.Date;
        return new FilterGroup
        {
            Logic = group.Logic,
            Nodes = group.Nodes.Select(n => ResolveDateNode(n, today)).ToList(),
        };
    }

    private static FilterNode ResolveDateNode(FilterNode n, DateTime today)
    {
        if (n.Condition is { } cond)
        {
            var duringGroup = ResolveDuringCondition(cond, today);
            return duringGroup is not null
                ? new FilterNode { Group = duringGroup }
                : new FilterNode { Condition = ResolveDateCondition(cond, today) };
        }
        return new FilterNode { Group = ResolveDateValueModeConditions(n.Group) };
    }

    private static FilterCondition ResolveDateCondition(FilterCondition cond, DateTime today)
    {
        const string isoFormat = "yyyy-MM-dd";
        string? resolved = cond.ValueMode switch
        {
            "today" => today.ToString(isoFormat),
            "yesterday" => today.AddDays(-1).ToString(isoFormat),
            "tomorrow" => today.AddDays(1).ToString(isoFormat),
            "pastDays" => int.TryParse(cond.Value, out var pd) ? today.AddDays(-pd).ToString(isoFormat) : null,
            "futureDays" => int.TryParse(cond.Value, out var fd) ? today.AddDays(fd).ToString(isoFormat) : null,
            _ => null,
        };
        if (resolved is null) return cond;
        return new FilterCondition
        {
            FieldId = cond.FieldId, Operator = cond.Operator, SubField = cond.SubField,
            Value = resolved, ValueMode = "literal", ValueFieldId = null,
        };
    }

    private static readonly HashSet<string> DuringUnits = new(StringComparer.OrdinalIgnoreCase)
        { "day", "week", "month", "quarter", "year" };

    /// <summary>Expands a during/notDuring condition into a literal date-range group — "is during"
    /// becomes an AND of {gte start, lte end}, "is not during" an OR of {lt start, gt end}.
    /// Returns null for every other operator, so ResolveDateNode falls back to the normal
    /// single-literal ResolveDateCondition path unchanged.</summary>
    private static FilterGroup? ResolveDuringCondition(FilterCondition cond, DateTime today)
    {
        var isDuring = string.Equals(cond.Operator, "during", StringComparison.OrdinalIgnoreCase);
        var isNotDuring = string.Equals(cond.Operator, "notDuring", StringComparison.OrdinalIgnoreCase);
        if (!isDuring && !isNotDuring) return null;

        var (count, unit) = ParseDuringValue(cond.Value);
        var (start, end) = ComputeDuringRange(cond.ValueMode, count, unit, today);
        const string isoFormat = "yyyy-MM-dd";

        FilterCondition Cond(string op, DateTime value) => new()
        {
            FieldId = cond.FieldId, SubField = cond.SubField,
            Operator = op, Value = value.ToString(isoFormat), ValueMode = "literal",
        };

        return isDuring
            ? new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = Cond("gte", start) }, new FilterNode { Condition = Cond("lte", end) }] }
            : new FilterGroup { Logic = "or", Nodes = [new FilterNode { Condition = Cond("lt", start) }, new FilterNode { Condition = Cond("gt", end) }] };
    }

    /// <summary>FilterCondition.Value for during/notDuring is "count:unit" (e.g. "2:month") — see
    /// DuringValue/parseDuringValue in the frontend's filter-condition-operators.ts. Malformed or
    /// missing input falls back to 1 week, the same permissive-default spirit as a blank
    /// pastDays/futureDays count elsewhere in this file.</summary>
    private static (int Count, string Unit) ParseDuringValue(string? value)
    {
        var parts = (value ?? "").Split(':');
        var unit = parts.Length == 2 && DuringUnits.Contains(parts[1]) ? parts[1] : "week";
        var count = parts.Length >= 1 && int.TryParse(parts[0], out var c) && c > 0 ? c : 1;
        return (count, unit);
    }

    /// <summary>Mirrors the frontend's pb-filter-value-control.component.ts duringRangePreview
    /// computation exactly (startOfPeriod/addUnits/endOfPeriod) — kept in lockstep so the preview
    /// shown at edit time matches what the report actually filters on at run time. "The previous/
    /// next N &lt;unit&gt;" excludes the current, still-in-progress period (e.g. today inside this
    /// week, "the previous 1 week" is the last full Mon-Sun week, not the 7 days ending today).</summary>
    private static (DateTime Start, DateTime End) ComputeDuringRange(string? valueMode, int count, string unit, DateTime today)
    {
        var curStart = StartOfDuringPeriod(today, unit);
        var curEnd = EndOfDuringPeriod(curStart, unit);

        if (string.Equals(valueMode, "duringPrevious", StringComparison.OrdinalIgnoreCase))
            return (AddDuringUnits(curStart, unit, -count), curStart.AddDays(-1));

        if (string.Equals(valueMode, "duringNext", StringComparison.OrdinalIgnoreCase))
            return (curEnd.AddDays(1), EndOfDuringPeriod(AddDuringUnits(curStart, unit, count), unit));

        // duringCurrent, and any unrecognized ValueMode (same permissive fallback as the rest of
        // this resolver: an unresolvable relative mode passes through as-is elsewhere; here there
        // is no "as-is" to fall back to, so "the current period" is the safest default).
        return (curStart, curEnd);
    }

    /// <summary>Monday-start week — matches RecordRepository.BuildGroupByExpr's existing "Week"
    /// grouping convention (DATEADD(WEEK, DATEDIFF(WEEK, 0, col), 0), which floors to Monday
    /// since SQL Server's day 0, 1900-01-01, was itself a Monday) — kept consistent with grouping
    /// elsewhere in the SAME report builder rather than, say, Quickbase's own Sunday-start weeks.</summary>
    private static DateTime StartOfDuringPeriod(DateTime d, string unit) => unit.ToLowerInvariant() switch
    {
        "day" => d.Date,
        "month" => new DateTime(d.Year, d.Month, 1),
        "quarter" => new DateTime(d.Year, ((d.Month - 1) / 3) * 3 + 1, 1),
        "year" => new DateTime(d.Year, 1, 1),
        _ => d.Date.AddDays(d.DayOfWeek == DayOfWeek.Sunday ? -6 : 1 - (int)d.DayOfWeek), // "week" + fallback
    };

    private static DateTime AddDuringUnits(DateTime d, string unit, int amount) => unit.ToLowerInvariant() switch
    {
        "day" => d.AddDays(amount),
        "month" => d.AddMonths(amount),
        "quarter" => d.AddMonths(amount * 3),
        "year" => d.AddYears(amount),
        _ => d.AddDays(amount * 7), // "week" + fallback
    };

    /// <summary>Last day of the SAME period that periodStart begins (periodStart must already be
    /// period-aligned — i.e. come from StartOfDuringPeriod/AddDuringUnits-on-a-period-start, not
    /// an arbitrary date).</summary>
    private static DateTime EndOfDuringPeriod(DateTime periodStart, string unit) => AddDuringUnits(periodStart, unit, 1).AddDays(-1);

    // Static (the user repository passed in) so a summary field's matching criteria resolve exactly
    // like a report's filter — see RelationalProjector.ResolveCriteriaValuesAsync.
    /// <param name="appUserRepo">When given (with <paramref name="appId"/>), contains / wildcard match on a user
    /// field search the app's users by name or email and become a filter on the matching users' ids — a user
    /// field stores an id, so the typed text could never be compared to it directly.</param>
    public static async Task<FilterGroup?> ResolveUserFieldValuesAsync(
        FilterGroup? group, IReadOnlyDictionary<long, AppField> fieldLookup, long currentUserId,
        Dictionary<Guid, long> guidCache, IUserRepository userRepo, CancellationToken ct,
        IAppUserRepository? appUserRepo = null, long appId = 0,
        string? currentUserEmail = null, string? currentUserName = null)
    {
        if (group is null) return null;
        var nodes = new List<FilterNode>();
        foreach (var n in group.Nodes)
        {
            FilterCondition? newCondition = n.Condition;
            if (n.Condition is { } cond)
            {
                var hasIdentity = !string.IsNullOrWhiteSpace(currentUserEmail) || !string.IsNullOrWhiteSpace(currentUserName);
                if (hasIdentity
                    && string.Equals(cond.Operator, "isCurrentUser", StringComparison.OrdinalIgnoreCase)
                    && fieldLookup.TryGetValue(cond.FieldId, out var formulaUserField)
                    && FormulaFilterSorter.EffectiveTypeCode(formulaUserField) is "Formula_User" or "Formula_ListUser")
                {
                    // A formula's user is whatever the formula builds: [Record Owner] gives the id, but ToUser([Name])
                    // or ToUser([Email]) gives the text itself as the user's identity. Match the current user by id,
                    // email or name so both kinds of formula work.
                    var listOfUsers = FormulaFilterSorter.EffectiveTypeCode(formulaUserField) == "Formula_ListUser";
                    var matchName = await IsNameUniqueInAppAsync(appUserRepo, appId, currentUserName, ct);
                    nodes.Add(new FilterNode { Group = CurrentUserIdentityGroup(cond.FieldId, listOfUsers, false, currentUserId, currentUserEmail, matchName ? currentUserName : null) });
                    continue;
                }
                else if (hasIdentity
                    && string.Equals(cond.Operator, "isNotCurrentUser", StringComparison.OrdinalIgnoreCase)
                    && fieldLookup.TryGetValue(cond.FieldId, out var formulaUserField2)
                    && FormulaFilterSorter.EffectiveTypeCode(formulaUserField2) is "Formula_User" or "Formula_ListUser")
                {
                    var listOfUsers2 = FormulaFilterSorter.EffectiveTypeCode(formulaUserField2) == "Formula_ListUser";
                    var matchName2 = await IsNameUniqueInAppAsync(appUserRepo, appId, currentUserName, ct);
                    nodes.Add(new FilterNode { Group = CurrentUserIdentityGroup(cond.FieldId, listOfUsers2, true, currentUserId, currentUserEmail, matchName2 ? currentUserName : null) });
                    continue;
                }
                else if (string.Equals(cond.Operator, "isCurrentUser", StringComparison.OrdinalIgnoreCase))
                {
                    // On a list of users (MultiUser, Formula_ListUser) "is the current user" means the list contains
                    // them — an exact match would only ever find lists holding nobody else.
                    var isUserList = fieldLookup.TryGetValue(cond.FieldId, out var userField) && userField.TypeCode is "MultiUser" or "Formula_ListUser";
                    newCondition = new FilterCondition { FieldId = cond.FieldId, Operator = isUserList ? "includes" : "eq", Value = currentUserId.ToString() };
                }
                else if (string.Equals(cond.Operator, "isNotCurrentUser", StringComparison.OrdinalIgnoreCase))
                {
                    var isUserList = fieldLookup.TryGetValue(cond.FieldId, out var userField) && userField.TypeCode is "MultiUser" or "Formula_ListUser";
                    nodes.Add(new FilterNode { Group = NotCurrentUserGroup(cond.FieldId, isUserList, currentUserId) });
                    continue;
                }
                else if (appUserRepo is not null
                    && UserTextOperators.Contains(cond.Operator)
                    && !string.IsNullOrWhiteSpace(cond.Value)
                    && (cond.ValueMode is null || string.Equals(cond.ValueMode, "literal", StringComparison.OrdinalIgnoreCase))
                    && fieldLookup.TryGetValue(cond.FieldId, out var textField)
                    && FormulaFilterSorter.EffectiveTypeCode(textField) is "User" or "MultiUser" or "Formula_User" or "Formula_ListUser")
                {
                    newCondition = await ResolveUserTextConditionAsync(
                        cond, FormulaFilterSorter.EffectiveTypeCode(textField)!, guidCache, userRepo, appUserRepo, appId, ct);
                }
                else if (!string.IsNullOrEmpty(cond.Value)
                    && (cond.ValueMode is null || string.Equals(cond.ValueMode, "literal", StringComparison.OrdinalIgnoreCase))
                    && fieldLookup.TryGetValue(cond.FieldId, out var field)
                    && (FormulaFilterSorter.EffectiveTypeCode(field) is "User" or "MultiUser" or "Formula_User" or "Formula_ListUser"))
                {
                    var parts = cond.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var resolvedParts = new List<string>(parts.Length);
                    foreach (var part in parts)
                    {
                        if (Guid.TryParse(part, out var guid))
                        {
                            var longId = await LookupUserIdAsync(guid, guidCache, userRepo, ct);
                            resolvedParts.Add(longId == -1 ? part : longId.ToString());
                        }
                        else
                        {
                            resolvedParts.Add(part);
                        }
                    }
                    newCondition = new FilterCondition
                    {
                        FieldId = cond.FieldId, Operator = cond.Operator, SubField = cond.SubField,
                        ValueMode = cond.ValueMode, ValueFieldId = cond.ValueFieldId,
                        Value = string.Join(",", resolvedParts),
                    };
                }
            }
            nodes.Add(new FilterNode
            {
                Condition = newCondition,
                Group = await ResolveUserFieldValuesAsync(n.Group, fieldLookup, currentUserId, guidCache, userRepo, ct, appUserRepo, appId, currentUserEmail, currentUserName),
            });
        }
        return new FilterGroup { Logic = group.Logic, Nodes = nodes };
    }

    /// <summary>A display name identifies a user only when nobody else in the app shares it - otherwise matching a
    /// formula's text against it would hand one user the records of another with the same name. Without the app's
    /// user list to check against, a name is never trusted.</summary>
    private static async Task<bool> IsNameUniqueInAppAsync(IAppUserRepository? appUserRepo, long appId, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || appUserRepo is null || appId <= 0) return false;
        var users = await appUserRepo.ListByAppIdAsync(appId, ct);
        return users
            .Where(u => string.Equals(u.UserName?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(u => u.UserPublicId)
            .Distinct()
            .Count() <= 1;
    }

    /// <summary>"is the current user" (or, negated, "is not the current user") for a formula that yields a user. Its
    /// value may be the user's id, email or - when it is unique in the app - name, so the field matches when it equals
    /// ANY of those (a list of users: when it includes any of them). Negated: blank, or none of them.</summary>
    public static FilterGroup CurrentUserIdentityGroup(
        long fieldId, bool isUserList, bool negate, long currentUserId, string? email, string? name)
    {
        var identities = new[] { currentUserId.ToString(), email, name }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matches = identities.Select(v => new FilterNode
        {
            Condition = new FilterCondition { FieldId = fieldId, Operator = isUserList ? "includes" : "eq", Value = v },
        }).ToList();

        if (!negate) return new FilterGroup { Logic = "or", Nodes = matches };

        var none = identities.Select(v => new FilterNode
        {
            Condition = new FilterCondition { FieldId = fieldId, Operator = isUserList ? "notIncludes" : "ne", Value = v },
        }).ToList();
        return new FilterGroup
        {
            Logic = "or",
            Nodes =
            [
                new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = "isEmpty" } },
                new FilterNode { Group = new FilterGroup { Logic = "and", Nodes = none } },
            ],
        };
    }

    /// <summary>"is not the current user": the field is blank, or (for a list of users) holds nobody but others /
    /// (for a single user) is someone else. A blank user counts as "not the current user", so the records nobody
    /// is assigned to stay visible — a plain "ne" would drop them (NULL never compares as different).</summary>
    public static FilterGroup NotCurrentUserGroup(long fieldId, bool isUserList, long currentUserId) => new()
    {
        Logic = "or",
        Nodes =
        [
            new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = "isEmpty" } },
            new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = isUserList ? "notIncludes" : "ne", Value = currentUserId.ToString() } },
        ],
    };

    /// <summary>A user's public id → the numeric id a user column stores; -1 when the user can't be found
    /// (deleted/unknown) — callers then leave the value as-is, which simply matches nothing.</summary>
    private static async Task<long> LookupUserIdAsync(Guid publicId, Dictionary<Guid, long> guidCache, IUserRepository userRepo, CancellationToken ct)
    {
        if (guidCache.TryGetValue(publicId, out var cached)) return cached;
        long id;
        try { id = (await userRepo.GetByPublicIdAsync(publicId, ct)).Id; }
        catch (Exception) { id = -1; }
        guidCache[publicId] = id;
        return id;
    }

    private static readonly HashSet<string> UserTextOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "contains", "notContains", "wildcard", "notWildcard",
    };

    /// <summary>contains / wildcard match on a user field. The user types a name or email; this finds the
    /// app's users it matches and rewrites the condition onto their ids: a single user is "in" (or "not in") that
    /// set, a list of users "includes" (or "does not include") any of them. No match at all becomes a condition
    /// that matches nothing (or, for the negative operators, everything).</summary>
    private static async Task<FilterCondition> ResolveUserTextConditionAsync(
        FilterCondition cond, string userType, Dictionary<Guid, long> guidCache,
        IUserRepository userRepo, IAppUserRepository appUserRepo, long appId, CancellationToken ct)
    {
        var wildcard = cond.Operator.Contains("ildcard", StringComparison.OrdinalIgnoreCase);
        var negate = cond.Operator.StartsWith("not", StringComparison.OrdinalIgnoreCase);
        var needle = cond.Value!.Trim();

        bool Matches(string? text) => !string.IsNullOrEmpty(text)
            && (wildcard ? FormulaFilterSorter.WildcardMatches(text, needle) : text.Contains(needle, StringComparison.OrdinalIgnoreCase));

        var ids = new List<long>();
        var users = await appUserRepo.ListByAppIdAsync(appId, ct);
        foreach (var publicId in users.Where(u => Matches(u.UserName) || Matches(u.UserEmail)).Select(u => u.UserPublicId).Distinct())
        {
            var id = await LookupUserIdAsync(publicId, guidCache, userRepo, ct);
            if (id != -1) ids.Add(id);
        }
        var idTexts = ids.Count == 0 ? new List<string> { "-1" } : ids.Select(i => i.ToString()).ToList();   // -1: no such user

        var isList = userType is "MultiUser" or "Formula_ListUser";
        return new FilterCondition
        {
            FieldId = cond.FieldId,
            SubField = cond.SubField,
            Operator = isList ? (negate ? "notIncludes" : "includes") : (negate ? "notIn" : "in"),
            Value = isList ? string.Join(",", idTexts) : JsonSerializer.Serialize(idTexts),
        };
    }

    internal static FilterGroup? MergeRuntimeFilters(
        FilterGroup? filterTree,
        IReadOnlyList<AppField> allFields,
        IReadOnlyList<(long FieldId, string Value, string? SubField)>? runtimeFilters)
    {
        if (runtimeFilters is not { Count: > 0 }) return filterTree;

        var runtimeNodes = new List<FilterNode>();

        var fieldDict = new Dictionary<long, AppField>();
        foreach (var f in allFields)
        {
            if (f.Fid.HasValue) fieldDict[(long)f.Fid.Value] = f;
            fieldDict[f.Id] = f;
        }

        // Group by (FieldId, SubField) to support:
        //  - Same-field multi-select → OR'd together
        //  - Different sub-fields of the same Address field → AND'd together
        var groupedFilters = runtimeFilters.GroupBy(rf => (rf.FieldId, rf.SubField ?? string.Empty));

        foreach (var group in groupedFilters)
        {
            var field = fieldDict.GetValueOrDefault(group.Key.FieldId);

            // Use eq for SingleSelect/Boolean/User/numeric types, contains for everything else
            // (free text). For Address sub-fields (JSON path), use eq by default.
            // Numeric types (Number/Currency/Percent/Rating/Duration) must use eq, not contains —
            // "contains" compiles to a SQL LIKE, and LIKE against a numeric column either fails
            // outright or does an imprecise substring match (clicking "10" would also match "110",
            // "210", "1.10", …). This runtime-filter path is exactly what chart drilldown clicks go
            // through (an exact grouped category/series value, e.g. a Quantity value on a chart
            // segment), so an inexact/broken match here was silently breaking drilldown for any
            // numeric category field.
            var firstSubField = string.IsNullOrEmpty(group.Key.Item2) ? null : group.Key.Item2;
            // A formula behaves like the stored type it returns (a number formula is matched exactly, a date
            // formula by day) — otherwise it would fall into "contains", an inexact substring match
            // (clicking 10 would also match 110).
            var filterType = FormulaFilterSorter.EffectiveTypeCode(field);
            var operatorName = filterType is "Date" or "DateTime" or "Formula_Date" or "Formula_DateTime"
                ? "date_eq"
                : filterType is "SingleSelect" or "Boolean" or "User" or "Address"
                    or "Number" or "Currency" or "Percent" or "Rating" or "Duration"
                    or "Formula_Number" or "Formula_Duration" or "Formula_Bool" or "Formula_User" ? "eq" : "contains";

            var values = group.Select(rf => rf.Value).ToList();

            // Defensively filter out unparseable values for numeric/date fields to prevent SQL cast errors
            if (filterType is "Number" or "Currency" or "Percent" or "Formula_Number")
            {
                values = values.Where(v => double.TryParse(v, out _)).ToList();
            }
            else if (filterType is "Date" or "DateTime" or "Formula_Date" or "Formula_DateTime")
            {
                values = values.Where(v => DateTime.TryParse(v, out _)).ToList();
            }

            if (values.Count == 0) continue;

            if (values.Count == 1)
            {
                runtimeNodes.Add(new FilterNode
                {
                    Condition = new FilterCondition { FieldId = group.Key.FieldId, Operator = operatorName, Value = values[0], SubField = firstSubField }
                });
            }
            else
            {
                var orNodes = values.Select(v => new FilterNode
                {
                    Condition = new FilterCondition { FieldId = group.Key.FieldId, Operator = operatorName, Value = v, SubField = firstSubField }
                }).ToList();

                runtimeNodes.Add(new FilterNode
                {
                    Group = new FilterGroup { Logic = "or", Nodes = orNodes }
                });
            }
        }

        if (runtimeNodes.Count == 0) return filterTree;

        return filterTree == null
            ? new FilterGroup { Logic = "and", Nodes = runtimeNodes }
            : new FilterGroup
            {
                Logic = "and",
                Nodes = [new FilterNode { Group = filterTree }, .. runtimeNodes]
            };
    }

    private async Task<PagedReportRunResult> RunSummaryAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        TableAccessContext access,
        ReportDefinition definition,
        int page, int pageSize,
        string? quickSearch,
        IReadOnlyList<(long FieldId, string Value, string? SubField)>? runtimeFilters,
        FilterGroup? savedAndRuntimeFilterTree,
        bool isMaskedPreview,
        CancellationToken ct)
    {
        var visibleFieldIds = access.VisibleFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();
        var fieldMap = allFields
            .Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // No group-by configured, or one of the Rows level fields is hidden/unknown — cannot
        // produce a meaningful summary.
        var rowGroupSpecs = ResolveRowGroupLevels(definition, fieldMap, visibleFieldIds);
        if (rowGroupSpecs is null)
        {
            return new PagedReportRunResult { Page = page, PageSize = pageSize };
        }

        // Only aggregate visible fields
        var visibleAggregations = definition.Aggregations
            .Where(a => visibleFieldIds.Contains(a.FieldId))
            .ToList();

        // Chart-only: an optional second grouping dimension ("Series / Group by") that splits each category
        // into multiple datasets. Ignored for plain Summary reports (definition.Chart is always null there).
        AppField? seriesField = null;
        if (definition.Chart?.SeriesFieldId is { } seriesFieldId
            && fieldMap.TryGetValue(seriesFieldId, out var resolvedSeriesField)
            && visibleFieldIds.Contains(seriesFieldId))
        {
            seriesField = resolvedSeriesField;
        }

        // Previously Summary/Chart reports ignored their own saved FilterTree entirely (only
        // role ViewFilter + dynamic/quick-search filters applied) — now AND everything together:
        // saved tree + ad-hoc runtime tree (already merged into savedAndRuntimeFilterTree by the
        // caller) + role ViewFilter + dynamic filters.
        var baseFilterTree = access.ViewFilter;
        if (savedAndRuntimeFilterTree is { Nodes.Count: > 0 })
        {
            baseFilterTree = baseFilterTree == null
                ? savedAndRuntimeFilterTree
                : new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = baseFilterTree }, new FilterNode { Group = savedAndRuntimeFilterTree }] };
        }
        var summaryFilterTree = MergeRuntimeFilters(baseFilterTree, allFields, runtimeFilters);

        // Gauge-only: when the goal is a live data value (not a fixed number), fold an extra
        // aggregation into the same grouped query so it comes back alongside the gauge's own
        // measure, then resolve one overall value from the per-group results below — summed
        // straight across groups for Sum, or count-weighted for Avg (Σ(avg_i × count_i) /
        // Σcount_i), since a plain average-of-per-group-averages would be wrong unless every
        // group happened to have the same size.
        SummaryAggregation? gaugeGoalAggregation = null;
        if (definition.Chart?.ChartType == "Gauge" && definition.Chart.GaugeGoalType == "DataValue"
            && definition.Chart.GaugeGoalFieldId is { } goalFieldId && visibleFieldIds.Contains(goalFieldId))
        {
            gaugeGoalAggregation = new SummaryAggregation { FieldId = goalFieldId, Function = definition.Chart.GaugeGoalFunction ?? "Sum" };
        }
        var aggregationsForQuery = gaugeGoalAggregation is null
            ? visibleAggregations
            : [.. visibleAggregations, gaugeGoalAggregation];

        var summarySort = ResolveSummarySort(definition, rowGroupSpecs, aggregationsForQuery);
        var seriesMode = definition.Chart?.SeriesMode ?? "EqualValues";
        // A Formula/Lookup/Summary field has no column, so SQL can't group by or aggregate it (it fails with
        // "Invalid column name" and the report never loads) — those reports are summarized in memory instead.
        var rows = InMemorySummarizer.IsRequired(rowGroupSpecs, aggregationsForQuery, seriesField, allFields, summaryFilterTree)
            ? await InMemorySummarizer.SummarizeAsync(
                _recordRepo, _relationalProjector, _formulaProjector,
                table, rowGroupSpecs, aggregationsForQuery, allFields,
                summaryFilterTree, access.RestrictToCreatedBy, seriesField, seriesMode, summarySort, ct)
            : await _recordRepo.SummarizeAsync(
                table, rowGroupSpecs, aggregationsForQuery, allFields,
                filterTree: summaryFilterTree, restrictToCreatedBy: access.RestrictToCreatedBy,
                seriesField: seriesField, seriesMode: seriesMode, sort: summarySort, ct: ct);

        // SummarizeAsync groups by the raw stored value — for a User field that's the numeric
        // user ID, not a display name (unlike RunTableAsync's rows, which already go through
        // ResolveUserNamesAsync). Resolve here too so Summary/Chart categories and series show
        // "Jane Doe" instead of "4". One lookup map per Rows level (a report can have several
        // User-typed levels, or the same level's field could be User-typed at any position).
        var rowGroupUserNames = new List<IReadOnlyDictionary<long, string>?>();
        for (var i = 0; i < rowGroupSpecs.Count; i++)
        {
            if (rowGroupSpecs[i].Field.TypeCode is not ("User" or "MultiUser" or "Formula_User")) { rowGroupUserNames.Add(null); continue; }
            var levelKeyName = $"GroupValue{i}";
            var ids = rows
                .Select(r => r.TryGetValue(levelKeyName, out var v) ? v : null)
                .Where(v => v is not null && long.TryParse(v.ToString(), out _))
                .Select(v => long.Parse(v!.ToString()!))
                .ToHashSet();
            rowGroupUserNames.Add(ids.Count > 0 ? await _userRepo.GetNamesByIdsAsync(ids, ct) : new Dictionary<long, string>());
        }
        IReadOnlyDictionary<long, string>? seriesUserNames = null;
        if (seriesField?.TypeCode is "User" or "MultiUser" or "Formula_User")
        {
            var ids = rows
                .Select(r => r.TryGetValue("SeriesValue", out var v) ? v : null)
                .Where(v => v is not null && long.TryParse(v.ToString(), out _))
                .Select(v => long.Parse(v!.ToString()!))
                .ToHashSet();
            if (ids.Count > 0)
                seriesUserNames = await _userRepo.GetNamesByIdsAsync(ids, ct);
        }

        decimal? resolvedGaugeGoalValue = null;
        if (gaugeGoalAggregation is not null && fieldMap.TryGetValue(gaugeGoalAggregation.FieldId, out var goalField))
        {
            var goalAlias = $"{gaugeGoalAggregation.Function}_{goalField.Name.Replace(" ", "_")}";
            if (string.Equals(gaugeGoalAggregation.Function, "Avg", StringComparison.OrdinalIgnoreCase))
            {
                decimal weightedSum = 0;
                long totalCount = 0;
                foreach (var row in rows)
                {
                    var count = row.TryGetValue("Count", out var c) && c is not null ? Convert.ToInt64(c) : 0;
                    var val = row.TryGetValue(goalAlias, out var v) && v is not null ? Convert.ToDecimal(v) : 0m;
                    weightedSum += val * count;
                    totalCount += count;
                }
                resolvedGaugeGoalValue = totalCount > 0 ? weightedSum / totalCount : 0m;
            }
            else
            {
                resolvedGaugeGoalValue = rows.Sum(row => row.TryGetValue(goalAlias, out var v) && v is not null ? Convert.ToDecimal(v) : 0m);
            }
        }

        // Build alias→unique-key map — NOT alias→fieldId. A user can aggregate the SAME field
        // with several different functions (e.g. Sum and Avg of Amount), and those columns
        // share a FieldId — keying by FieldId alone collapses them onto the same row/column
        // slot, so every one of them silently displays whichever aggregation's value happened
        // to be written last. Key is a synthetic, always-unique-per-column identifier instead.
        // Identify columns displayed as percent-of-total and compute their totals.
        var aggAliasToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var percentAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < visibleAggregations.Count; i++)
        {
            var agg = visibleAggregations[i];
            if (!fieldMap.TryGetValue(agg.FieldId, out var aggField)) continue;
            var alias = $"{agg.Function}_{aggField.Name.Replace(" ", "_")}";
            aggAliasToKey[alias] = $"agg{i}_{agg.FieldId}";
            if (agg.DisplayAs == "PercentOfColumnTotal")
            {
                percentAliases.Add(alias);
                columnTotals[alias] = rows.Sum(row =>
                    row.TryGetValue(alias, out var v) ? Convert.ToDouble(v ?? 0) : 0.0);
            }
        }

        // Remap SQL alias keys to unique row keys; apply percent transform where configured.
        // Single-level reports (every Chart report, and every Summary report saved before
        // RowGroupLevels existed) keep the original bare-fieldId key — chart-config.util.ts's
        // buildChartJsConfig hardcodes `catKey = String(groupByFieldId)` rather than looking the
        // column up by its actual key, so changing this format for the single-level case breaks
        // every chart. Only 2+ levels switch to "row{i}_{fieldId}": Rows allows the SAME field to
        // appear at more than one level (e.g. "Category" as both level 2 and the last level),
        // which a plain fieldId key would collide on — the index makes each level's slot unique
        // (same convention as aggAliasToKey below), and is safe there since Chart/legacy-single
        // code never reaches the multi-level path (ChartReportConfigValidator forbids
        // RowGroupLevels outright).
        var rowGroupKeys = rowGroupSpecs.Count == 1
            ? [(rowGroupSpecs[0].Field.Fid ?? rowGroupSpecs[0].Field.Id).ToString()]
            : rowGroupSpecs.Select((g, i) => $"row{i}_{g.Field.Fid ?? g.Field.Id}").ToList();
        var seriesKey = seriesField is not null ? (seriesField.Fid ?? seriesField.Id).ToString() : null;
        var items = rows.Select(row =>
        {
            var fields = new Dictionary<string, object?>();
            for (var i = 0; i < rowGroupSpecs.Count; i++)
            {
                fields[rowGroupKeys[i]] = ResolveGroupOrSeriesValue(
                    row.TryGetValue($"GroupValue{i}", out var gv) ? gv : null, rowGroupUserNames[i]);
            }
            fields["0"] = row.TryGetValue("Count", out var cnt) ? cnt : null;
            if (seriesKey is not null)
                fields[seriesKey] = ResolveGroupOrSeriesValue(
                    row.TryGetValue("SeriesValue", out var sv) ? sv : null, seriesUserNames);
            foreach (var (alias, key) in aggAliasToKey)
            {
                if (!row.TryGetValue(alias, out var val)) continue;
                if (percentAliases.Contains(alias) && columnTotals.TryGetValue(alias, out var total) && total != 0)
                    fields[key] = Math.Round(Convert.ToDouble(val ?? 0) / total * 100, 2);
                else
                    fields[key] = val;
            }
            return new RecordResult { Id = Guid.Empty, CreatedOn = DateTime.UtcNow, Fields = fields };
        }).ToList();

        // Synthetic columns: one per Rows group level, then Count, then (optional) crosstab
        // series field, then one per visible aggregation. Key must match the keys used in
        // `fields` above, or the frontend can't look up the values by column — FieldId alone is
        // NOT sufficient here (see rowGroupKeys/aggAliasToKey above).
        var columns = new List<ReportColumnInfo>();
        for (var i = 0; i < rowGroupSpecs.Count; i++)
        {
            var f = rowGroupSpecs[i].Field;
            columns.Add(new ReportColumnInfo { FieldId = f.Fid ?? f.Id, Key = rowGroupKeys[i], Name = string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label, TypeCode = f.TypeCode });
        }
        columns.Add(new() { FieldId = 0, Key = "0", Name = "Count", TypeCode = "Number" });
        if (seriesField is not null)
        {
            columns.Add(new ReportColumnInfo { FieldId = seriesField.Fid ?? seriesField.Id, Key = seriesKey!, Name = string.IsNullOrWhiteSpace(seriesField.Label) ? seriesField.Name : seriesField.Label, TypeCode = seriesField.TypeCode });
        }
        for (var i = 0; i < visibleAggregations.Count; i++)
        {
            var agg = visibleAggregations[i];
            if (fieldMap.TryGetValue(agg.FieldId, out var aggField))
            {
                var fieldName = string.IsNullOrWhiteSpace(aggField.Label) ? aggField.Name : aggField.Label;
                var label = agg.DisplayAs == "PercentOfColumnTotal"
                    ? $"{agg.Function} of {fieldName} (%)"
                    : $"{agg.Function} of {fieldName}";
                // Max/Min return a value from the source field's own domain (e.g. Max of a Date
                // field is a date, not a count/sum) — the frontend needs the real TypeCode to
                // render it correctly (formatDate vs formatNumber), not the generic "Number"
                // every other aggregation function actually produces.
                var columnTypeCode = agg.Function is "Max" or "Min" ? aggField.TypeCode : "Number";
                columns.Add(new ReportColumnInfo { FieldId = aggField.Fid ?? aggField.Id, Key = $"agg{i}_{agg.FieldId}", Name = label, TypeCode = columnTypeCode });
            }
        }

        return new PagedReportRunResult
        {
            Items = items,
            Columns = columns,
            TotalCount = rows.Count,
            Page = 1,
            PageSize = rows.Count > 0 ? rows.Count : pageSize,
            IsDataMasked = isMaskedPreview,
            ResolvedGaugeGoalValue = resolvedGaugeGoalValue,
        };
    }

    /// <summary>Resolves Summary's "Rows" group levels — RowGroupLevels (the chained "Group by X,
    /// then by Y, ...") when non-empty, else the legacy single GroupByFieldId/GroupByMode as a
    /// 1-element list (same "empty means legacy fallback" convention TableSortGroup uses for
    /// Table reports). Returns null when there's no grouping configured at all, or when any
    /// level's field is unknown/hidden — RunSummaryAsync/ExportSummaryAsync treat null as "return
    /// an empty result", matching the pre-existing single-field behavior. Shared with
    /// ExportReportQueryHandler (same convention as ResolveUserNamesAsync below).</summary>
    internal static List<(AppField Field, string Mode)>? ResolveRowGroupLevels(
        ReportDefinition definition, Dictionary<long, AppField> fieldMap, HashSet<long> visibleFieldIds)
    {
        var rawLevels = new List<(long FieldId, string Mode)>();
        if (definition.RowGroupLevels.Count > 0)
        {
            foreach (var l in definition.RowGroupLevels)
                rawLevels.Add((l.FieldId, string.IsNullOrWhiteSpace(l.GroupByMode) ? "EqualValues" : l.GroupByMode));
        }
        else if (definition.GroupByFieldId.HasValue)
        {
            rawLevels.Add((definition.GroupByFieldId.Value, string.IsNullOrWhiteSpace(definition.GroupByMode) ? "EqualValues" : definition.GroupByMode));
        }
        if (rawLevels.Count == 0) return null;

        var resolved = new List<(AppField Field, string Mode)>();
        foreach (var (fieldId, mode) in rawLevels)
        {
            if (!fieldMap.TryGetValue(fieldId, out var field) || !visibleFieldIds.Contains(fieldId)) return null;
            resolved.Add((field, mode));
        }
        return resolved;
    }

    /// <summary>Resolves Summary's SummarySortFields (each identifying a Rows level by index, the
    /// locked Count column, or an aggregation by field+function) into the SQL-facing
    /// SummarizeSortSpec list SummarizeAsync actually understands (Rows level → position in
    /// rowGroupSpecs, aggregation → position in the exact aggregationsForQuery list being passed
    /// to that same call). An entry whose target no longer resolves (e.g. an aggregation that got
    /// removed, or a Rows level index now out of range) is silently dropped rather than failing
    /// the whole report — same "best effort, don't 500 over stale config" spirit as
    /// ResolveRowGroupLevels' sibling checks. Returns null when there's nothing to sort by,
    /// leaving SummarizeAsync's pre-existing default order (ascending by every Rows level)
    /// unchanged — this only ever runs for genuine Summary reports since Chart/Table are
    /// validator-forbidden from populating SummarySortFields at all. Shared with
    /// ExportReportQueryHandler (same convention as ResolveRowGroupLevels/ResolveUserNamesAsync
    /// above).</summary>
    internal static List<SummarizeSortSpec>? ResolveSummarySort(
        ReportDefinition definition, List<(AppField Field, string Mode)> rowGroupSpecs, List<SummaryAggregation> aggregationsForQuery)
    {
        if (definition.SummarySortFields.Count == 0) return null;

        var result = new List<SummarizeSortSpec>();
        foreach (var s in definition.SummarySortFields)
        {
            switch (s.Target)
            {
                case "RowLevel":
                    if (s.LevelIndex is { } levelIndex && levelIndex >= 0 && levelIndex < rowGroupSpecs.Count)
                        result.Add(new SummarizeSortSpec(SummarizeSortTarget.GroupLevel, levelIndex, s.Desc));
                    break;
                case "Count":
                    result.Add(new SummarizeSortSpec(SummarizeSortTarget.Count, 0, s.Desc));
                    break;
                case "Aggregation":
                    var aggIndex = aggregationsForQuery.FindIndex(a =>
                        a.FieldId == s.AggregationFieldId && string.Equals(a.Function, s.AggregationFunction, StringComparison.OrdinalIgnoreCase));
                    if (aggIndex >= 0)
                        result.Add(new SummarizeSortSpec(SummarizeSortTarget.Aggregation, aggIndex, s.Desc));
                    break;
            }
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Swaps a raw grouped/series value for its resolved display name when one was
    /// looked up (User/MultiUser group-by or series fields in Summary/Chart reports) — returns
    /// the value unchanged for every other field type, or if the id wasn't in the lookup
    /// (e.g. a deleted user).</summary>
    /// <summary>
    /// Returns "{id}|{name}" for a resolved User/MultiUser group/series value — same composite
    /// convention GetDistinctFieldValuesQueryHandler already uses for the filter dropdown — so
    /// the frontend can display the name while still holding the raw id it needs to build a
    /// correct drilldown filter. The physical column stores the plain numeric core.[User].Id
    /// (see MergeRuntimeFilters/BuildConditionClause), not the display name; a drilldown click
    /// that submitted the display name back as the filter value always matched zero rows.
    /// </summary>
    private static object? ResolveGroupOrSeriesValue(object? rawValue, IReadOnlyDictionary<long, string>? names)
    {
        if (names is null || rawValue is null) return rawValue;
        var raw = rawValue.ToString()!;
        return long.TryParse(raw, out var id) && names.TryGetValue(id, out var name) ? $"{raw}|{name}" : rawValue;
    }

    /// <param name="computed">The rows' projected formula values. When supplied, the user ids a
    /// Formula_User field evaluated to join the same single batched lookup as the stored User
    /// columns, so a formula result (e.g. [Record Owner]) resolves to a name for free.</param>
    internal static async Task<IReadOnlyDictionary<long, string>> ResolveUserNamesAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<AppField> fields,
        IUserRepository userRepo,
        CancellationToken ct,
        IEnumerable<IReadOnlyDictionary<long, object?>>? computed = null)
    {
        // A Lookup of a parent User-typed field carries the parent's raw user id(s) as its
        // projected value — resolve it like the source field it pulls down.
        var userLookupTypes = fields
            .Where(f => f.Fid.HasValue && f.TypeCode == "Lookup")
            .Select(f => (Fid: (long)f.Fid!.Value, Type: RecordResult.UserLookupSourceType(f)))
            .Where(x => x.Type is not null)
            .ToList();

        var hasUserFields = userLookupTypes.Count > 0 || fields.Any(f =>
            f.TypeCode is "User" or "MultiUser" or "File" or "Formula_User" or "Formula_ListUser" ||
            (f.IsSystem && f.PhysicalColumnName is "CreatedBy" or "ModifiedBy"));

        if (!hasUserFields) return new Dictionary<long, string>();

        var ids = new HashSet<long>();
        if (computed is not null)
        {
            var formulaUserFids = fields.Where(f => f.TypeCode == "Formula_User" && f.Fid.HasValue)
                .Select(f => (long)f.Fid!.Value)
                .Concat(userLookupTypes.Where(x => x.Type is "User" or "Formula_User").Select(x => x.Fid))
                .ToList();
            var formulaUserListFids = fields.Where(f => f.TypeCode == "Formula_ListUser" && f.Fid.HasValue)
                .Select(f => (long)f.Fid!.Value)
                .Concat(userLookupTypes.Where(x => x.Type == "Formula_ListUser").Select(x => x.Fid))
                .ToList();
            // A looked-up MultiUser is the parent's stored JSON array of ids ("[12,15]").
            var multiUserLookupFids = userLookupTypes.Where(x => x.Type == "MultiUser").Select(x => x.Fid).ToList();
            foreach (var values in computed)
            {
                foreach (var fid in formulaUserFids)
                    if (values.TryGetValue(fid, out var v) && v is not null && long.TryParse(v.ToString(), out var uid))
                        ids.Add(uid);
                // A Formula_ListUser evaluates to a list of user ids (see FormulaRawValue.ToRaw).
                foreach (var fid in formulaUserListFids)
                    if (values.TryGetValue(fid, out var lv) && lv is IEnumerable<string> listIds)
                        foreach (var raw in listIds)
                            if (long.TryParse(raw, out var luid)) ids.Add(luid);
                foreach (var fid in multiUserLookupFids)
                {
                    if (!values.TryGetValue(fid, out var mv) || mv is null) continue;
                    var str = mv.ToString()!;
                    if (str.TrimStart().StartsWith('['))
                    {
                        try
                        {
                            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<long>>(str);
                            if (parsed != null) foreach (var pid in parsed) ids.Add(pid);
                        }
                        catch { }
                    }
                    else if (long.TryParse(str, out var muid)) ids.Add(muid);
                }
            }
        }
        foreach (var row in rows)
        {
            // System user columns (stored as long)
            foreach (var col in new[] { "CreatedBy", "ModifiedBy" })
            {
                if (row.TryGetValue(col, out var v) && v is not null && long.TryParse(v.ToString(), out var id))
                    ids.Add(id);
            }
            // User/MultiUser field columns
            foreach (var f in fields.Where(f => f.TypeCode is "User" or "MultiUser" && f.Fid.HasValue))
            {
                var col = PowerBase.Domain.Constants.PhysicalNaming.ColumnName(f.Fid!.Value);
                if (!row.TryGetValue(col, out var val) || val is null) continue;
                var str = val.ToString()!;
                if (str.TrimStart().StartsWith('['))
                {
                    try
                    {
                        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<long>>(str);
                        if (parsed != null) foreach (var pid in parsed) ids.Add(pid);
                    }
                    catch { }
                }
                else if (long.TryParse(str, out var uid)) ids.Add(uid);
            }
        }

        return await userRepo.GetNamesByIdsAsync(ids, ct);
    }

    /// <summary>Sibling of <see cref="ResolveUserNamesAsync"/>, but resolves each User/MultiUser
    /// field's internal long id to its public Guid instead of its display name. Used only by
    /// GetRecordQueryHandler (the single-record fetch backing the Add/Edit Record form) — every
    /// other read path (list/table, Summary/Chart, export) wants a ready-to-display name and keeps
    /// using ResolveUserNamesAsync. The Edit form's User/MultiUser picker is keyed by userPublicId
    /// (see AppUserPickerResponse / UserFieldValueResolver, which resolves the picker's submitted
    /// Guid back to this same long id on save) — handing it a display name instead of a Guid left
    /// the picker unable to match any option, showing empty ("Select User") no matter what was
    /// actually saved. The system User fields (Record Owner / Last Modified By) are covered via
    /// their own physical column; the CreatedByName/ModifiedBy name properties still come from
    /// ResolveUserNamesAsync regardless.</summary>
    internal static async Task<IReadOnlyDictionary<long, Guid>> ResolveUserPublicIdsAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<AppField> fields,
        IUserRepository userRepo,
        CancellationToken ct)
    {
        var hasUserFields = fields.Any(f => f.TypeCode is "User" or "MultiUser");
        if (!hasUserFields) return new Dictionary<long, Guid>();

        var ids = new HashSet<long>();
        foreach (var row in rows)
        {
            foreach (var f in fields.Where(f => f.TypeCode is "User" or "MultiUser" && f.Fid.HasValue))
            {
                // Record Owner / Last Modified By are User-typed but store under their own system
                // column; RecordResult.FromRow routes them through this map too, so without their
                // ids here they fell back to the raw numeric id.
                var col = f.IsSystem && !string.IsNullOrEmpty(f.PhysicalColumnName)
                    ? f.PhysicalColumnName!
                    : PowerBase.Domain.Constants.PhysicalNaming.ColumnName(f.Fid!.Value);
                if (!row.TryGetValue(col, out var val) || val is null) continue;
                var str = val.ToString()!;
                if (str.TrimStart().StartsWith('['))
                {
                    try
                    {
                        var parsed = System.Text.Json.JsonSerializer.Deserialize<List<long>>(str);
                        if (parsed != null) foreach (var pid in parsed) ids.Add(pid);
                    }
                    catch { }
                }
                else if (long.TryParse(str, out var uid)) ids.Add(uid);
            }
        }

        return await userRepo.GetPublicIdsByIdsAsync(ids, ct);
    }

    /// <summary>
    /// Extracts field values from a row dictionary where keys can be physical column names (e.g. "f_103", "Id", "CreatedBy"),
    /// numeric strings ("103"), or "fid_103", and maps them by both Fid (long) and Field Id (long) for PipelineFilterEvaluator.
    /// Handles null values, DBNull, dates, and unexpected formats safely.
    /// </summary>
    public static IReadOnlyDictionary<long, object?> ExtractFieldValuesByFid(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<AppField> fields)
    {
        var map = new Dictionary<long, object?>();
        if (row == null || row.Count == 0) return map;

        // 1. Map fields from the table's field list
        foreach (var field in fields)
        {
            var fid = field.Fid.HasValue ? (long)field.Fid.Value : (long?)null;
            var id = field.Id;

            object? val = null;
            bool found = false;

            // Physical column name: e.g. "f_103", "CreatedOn", "CreatedBy", "Id", etc.
            var colName = EncryptedRowFilter.Column(field);
            if (row.TryGetValue(colName, out val))
            {
                found = true;
            }
            else if (fid.HasValue && row.TryGetValue(PhysicalNaming.ColumnName((int)fid.Value), out val))
            {
                found = true;
            }
            else if (fid.HasValue && row.TryGetValue(fid.Value.ToString(CultureInfo.InvariantCulture), out val))
            {
                found = true;
            }
            else if (row.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out val))
            {
                found = true;
            }
            else if (!string.IsNullOrWhiteSpace(field.Name) && row.TryGetValue(field.Name, out val))
            {
                found = true;
            }
            else if (fid.HasValue && row.TryGetValue($"fid_{fid.Value}", out val))
            {
                found = true;
            }

            if (found)
            {
                if (val is DBNull) val = null;
                if (val is DateTime dt) val = dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

                if (fid.HasValue) map[fid.Value] = val;
                map[id] = val;
            }
        }

        // 2. Map standard system fields if present in row
        if (row.TryGetValue("Id", out var idVal) && idVal is not DBNull)
        {
            map[3] = idVal; // FID 3 is Id / RecordId
        }
        if (row.TryGetValue("CreatedOn", out var createdOnVal) && createdOnVal is not DBNull)
        {
            map[1] = createdOnVal is DateTime dt ? dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : createdOnVal;
        }
        if (row.TryGetValue("ModifiedOn", out var modOnVal) && modOnVal is not DBNull)
        {
            map[2] = modOnVal is DateTime dt ? dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : modOnVal;
        }
        if (row.TryGetValue("CreatedBy", out var createdByVal) && createdByVal is not DBNull)
        {
            map[4] = createdByVal;
        }
        if (row.TryGetValue("ModifiedBy", out var modByVal) && modByVal is not DBNull)
        {
            map[5] = modByVal;
        }

        // 3. Scan row for any unmapped keys like "f_103", "fid_103", or numeric strings
        foreach (var kv in row)
        {
            var key = kv.Key;
            var val = kv.Value is DBNull ? null : kv.Value;
            if (val is DateTime dt) val = dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

            if (key.StartsWith("f_", StringComparison.OrdinalIgnoreCase) && long.TryParse(key.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fNum))
            {
                map.TryAdd(fNum, val);
            }
            else if (key.StartsWith("fid_", StringComparison.OrdinalIgnoreCase) && long.TryParse(key.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fidNum))
            {
                map.TryAdd(fidNum, val);
            }
            else if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var directNum))
            {
                map.TryAdd(directNum, val);
            }
        }

        return map;
    }
}
