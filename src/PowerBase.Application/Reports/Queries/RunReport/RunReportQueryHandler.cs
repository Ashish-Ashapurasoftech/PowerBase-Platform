using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
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
}

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
        ILogger<RunReportQueryHandler> logger)
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
    }

    public async Task<PagedReportRunResult> HandleAsync(RunReportQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var report = await _reportRepo.GetVisibleReportAsync(query.ReportPublicId, ct)
            ?? throw new NotFoundException("Report", query.ReportPublicId);
        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
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

        // Enforce the report's Dynamic Filter configuration server-side: when DynamicFilterType
        // is "Custom", only the fields the report designer picked may be used as a runtime
        // dynamic filter — previously this was a UI-only hint (any filterable field worked
        // regardless of the saved config). Silently drop disallowed params (same "silent
        // intersection" behavior the Columns restriction already uses below) rather than 400ing,
        // since a stale client could otherwise break entirely on a report whose Custom field set
        // was narrowed after the client loaded it.
        var effectiveRuntimeFilters = FilterRuntimeFiltersByDynamicFilterConfig(definition, query.RuntimeFilters);

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

        // Runtime filter tree (Advanced builder / per-column filters), AND'd on top of the
        // saved tree — role ViewFilter and dynamic/quick-search filters are merged in further
        // down (RunTableAsync) / below (RunSummaryAsync).
        if (query.RuntimeFilterTree is { Nodes.Count: > 0 })
        {
            filterTree = filterTree == null
                ? query.RuntimeFilterTree
                : new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = filterTree }, new FilterNode { Group = query.RuntimeFilterTree }] };
        }

        if (report.ReportType is "Summary" or "Chart")
            return await RunSummaryAsync(table, allFields, access, definition, page, pageSize, query.QuickSearch, effectiveRuntimeFilters, filterTree, isMaskedPreview, ct);

        return await RunTableAsync(table, allFields, access, definition, page, pageSize, filterTree, sortFields,
            effectiveRuntimeFilters, query.QuickSearch, query.QuickSearchFieldIds, query.QuickSearchExact, isMaskedPreview, ct);
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
            selectedFields = definition.Columns
                .Where(id => fieldMap.ContainsKey(id) && visibleFieldIds.Contains(id))
                .Select(id => fieldMap[id])
                .ToList();
        }

        if (selectedFields.Count == 0 && definition.ColumnsMode == "Default")
        {
            var defaultReport = await _reportRepo.GetDefaultByTableAsync(table.PublicId, ct);
            var defaultColumnIds = defaultReport is null
                ? []
                : (JsonSerializer.Deserialize<ReportDefinition>(defaultReport.Definition) ?? new ReportDefinition()).Columns;

            if (defaultColumnIds.Count > 0)
            {
                var fieldMap = allFields
                    .Where(f => f.Fid.HasValue)
                    .GroupBy(f => (long)f.Fid!.Value)
                    .ToDictionary(g => g.Key, g => g.First());
                selectedFields = defaultColumnIds
                    .Where(id => fieldMap.ContainsKey(id) && visibleFieldIds.Contains(id))
                    .Select(id => fieldMap[id])
                    .ToList();
            }
        }

        if (selectedFields.Count == 0)
        {
            selectedFields = allFields.Where(f => f.Fid.HasValue && f.IsReportable && visibleFieldIds.Contains((long)f.Fid!.Value)).ToList();
        }
        if (selectedFields.Count == 0)
        {
            selectedFields = allFields.Where(f => f.Fid.HasValue && visibleFieldIds.Contains((long)f.Fid!.Value)).ToList();
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
                    // Route query to Azure AI Search to bypass SQL encryption limitations.
                    var aiMatches = await _searchService.SearchRecordsAsync(_queryContext.TenantId, table.Id, quickSearch, ct);
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
                        // Quick search matched nothing. The intersection of zero AI matches with
                        // any ViewFilter subset is always zero, so return empty regardless of
                        // whether the role has a ViewFilter — same reasoning as the OData path below.
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
                    .Where(f => !PhysicalNaming.IsComputedTypeCode(f.TypeCode) &&
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

        // Apply OData-based filter via Azure AI Search for user-applied filters only.
        // Role ViewFilter is intentionally excluded from OData/AI Search routing — it is always
        // enforced via SQL below to guarantee correctness regardless of AI Search index freshness.
        // GAP #3: Only when IsGridSearchEnabled. GAP #4: Falls back to raw SQL tree on AI failure.
        if (_searchService.IsGridSearchEnabled && userFilterTree != null && allFields.Any(f => f.IsSearchable || f.IsFilterable))
        {
            var odata = OData.ODataFilterBuilder.Build(userFilterTree, allFields);
            if (!string.IsNullOrWhiteSpace(odata))
            {
                try
                {
                    // GAP #2: Cap results + use direct ID query instead of OR node explosion.
                    var aiMatches = await _searchService.SearchRecordsByFilterAsync(_queryContext.TenantId, table.Id, odata, ct);
                    var cappedMatches = aiMatches.Count > AiSearchMaxResults
                        ? aiMatches.Take(AiSearchMaxResults).ToList()
                        : aiMatches;

                    if (cappedMatches.Count == 0)
                    {
                        // User-applied filters matched nothing in AI Search.
                        // If there is also a role ViewFilter, still return empty — the intersection
                        // of zero AI matches with any ViewFilter subset is always zero.
                        return new PagedReportRunResult { Page = page, PageSize = pageSize, Columns = columns };
                    }

                    // Rebuild userFilterTree: AND with matched IDs only (replaces original tree —
                    // AI Search has already applied the user filter, so we just restrict to its results).
                    userFilterTree = await BuildAiIdFilterAsync(table, cappedMatches, null, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // GAP #4: AI Search unavailable — use the original SQL filter tree as-is.
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
        // Determine which field IDs are formula (compute-on-read, no physical column)
        // OR are encrypted, so we must filter/sort them in memory instead of SQL.
        var formulaFids = allFields
            .Where(f => (f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings)) || 
                        (f.IsEncrypted && f.Fid.HasValue))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();

        var hasFormulaFilters = FormulaFilterSorter.TreeContainsFormulaField(filterTree, formulaFids);
        var hasFormulaSorts   = sortFields.Any(s => formulaFids.Contains(s.FieldId));

        IReadOnlyList<RecordResult> items;
        int total;

        if (hasFormulaFilters || hasFormulaSorts)
        {
            // Strip formula conditions from the SQL filter tree; keep them for in-memory pass.
            var (physicalFilterTree, formulaConditions) = FormulaFilterSorter.SplitFilterTree(filterTree, formulaFids);

            // When sorts include formula fields, skip SQL ORDER BY (we'll re-sort everything in-memory).
            IReadOnlyList<SortSpec> sqlSorts = hasFormulaSorts ? [] : sortFields;

            // Fetch ALL rows matching the physical filters (no SQL pagination).
            const int maxRows = 50_000;
            var allRows = await _recordRepo.ListAsync(table, allFields, 1, maxRows,
                physicalFilterTree, sqlSorts,
                restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

            var allRelational = await _relationalProjector.ProjectAsync(table, allFields, allRows, ct);
            var allComputed = _formulaProjector.Project(allFields, allRows, allRelational, table);
            var pairs = allRows.Zip(allComputed, (r, c) => (Row: r, Computed: c)).ToList();

            // Apply formula-field conditions in memory.
            if (formulaConditions != null)
                pairs = FormulaFilterSorter.ApplyFormulaFilters(pairs, formulaConditions, allFields);

            // If any sort key is a formula field, re-sort all rows in memory (covers all sort keys).
            if (hasFormulaSorts)
                pairs = FormulaFilterSorter.ApplySort(pairs, sortFields, allFields);

            total = pairs.Count;

            // Paginate in memory.
            var pagePairs = pairs.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var userNames = await ResolveUserNamesAsync(pagePairs.Select(p => p.Row), allFields, _userRepo, ct);
            items = pagePairs.Select(p => RecordResult.FromRow(p.Row, selectedFields, userNames, p.Computed)).ToList();
        }
        else
        {
            // Normal path — SQL handles pagination, sorting, and filtering entirely.
            var rows = await _recordRepo.ListAsync(table, allFields, page, pageSize, filterTree, sortFields,
                restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
            total = await _recordRepo.CountAsync(table, allFields, filterTree,
                restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

            var userNames = await ResolveUserNamesAsync(rows, allFields, _userRepo, ct);
            var relational = await _relationalProjector.ProjectAsync(table, allFields, rows, ct);
            var computed = _formulaProjector.Project(allFields, rows, relational, table);
            items = rows.Select((row, i) => RecordResult.FromRow(row, selectedFields, userNames, computed[i])).ToList();
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
        };
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
            var operatorName = field?.TypeCode is "Date" or "DateTime"
                ? "date_eq"
                : field?.TypeCode is "SingleSelect" or "Boolean" or "User" or "Address"
                    or "Number" or "Currency" or "Percent" or "Rating" or "Duration" ? "eq" : "contains";

            var values = group.Select(rf => rf.Value).ToList();

            // Defensively filter out unparseable values for numeric/date fields to prevent SQL cast errors
            if (field?.TypeCode is "Number" or "Currency" or "Percent")
            {
                values = values.Where(v => double.TryParse(v, out _)).ToList();
            }
            else if (field?.TypeCode is "Date" or "DateTime")
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
        if (!definition.GroupByFieldId.HasValue)
        {
            // No group-by configured — return empty result
            return new PagedReportRunResult { Page = page, PageSize = pageSize };
        }

        var visibleFieldIds = access.VisibleFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();
        var fieldMap = allFields
            .Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        // If the group-by field is hidden, cannot produce a meaningful summary
        if (!fieldMap.TryGetValue(definition.GroupByFieldId.Value, out var groupByField) || !visibleFieldIds.Contains(definition.GroupByFieldId.Value))
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

        var rows = await _recordRepo.SummarizeAsync(
            table, groupByField, aggregationsForQuery, allFields, definition.GroupByMode,
            filterTree: summaryFilterTree, restrictToCreatedBy: access.RestrictToCreatedBy,
            seriesField: seriesField, seriesMode: definition.Chart?.SeriesMode ?? "EqualValues", ct: ct);

        // SummarizeAsync groups by the raw stored value — for a User field that's the numeric
        // user ID, not a display name (unlike RunTableAsync's rows, which already go through
        // ResolveUserNamesAsync). Resolve here too so Summary/Chart categories and series show
        // "Jane Doe" instead of "4".
        IReadOnlyDictionary<long, string>? groupUserNames = null;
        if (groupByField.TypeCode is "User" or "MultiUser")
        {
            var ids = rows
                .Select(r => r.TryGetValue("GroupValue", out var v) ? v : null)
                .Where(v => v is not null && long.TryParse(v.ToString(), out _))
                .Select(v => long.Parse(v!.ToString()!))
                .ToHashSet();
            if (ids.Count > 0)
                groupUserNames = await _userRepo.GetNamesByIdsAsync(ids, ct);
        }
        IReadOnlyDictionary<long, string>? seriesUserNames = null;
        if (seriesField?.TypeCode is "User" or "MultiUser")
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

        // Remap SQL alias keys to unique row keys; apply percent transform where configured
        var groupKey = (groupByField.Fid ?? groupByField.Id).ToString();
        var seriesKey = seriesField is not null ? (seriesField.Fid ?? seriesField.Id).ToString() : null;
        var items = rows.Select(row =>
        {
            var fields = new Dictionary<string, object?>();
            fields[groupKey] = ResolveGroupOrSeriesValue(
                row.TryGetValue("GroupValue", out var gv) ? gv : null, groupUserNames);
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

        // Synthetic columns: group-by field + Count + (Chart-only) series field + one per visible
        // aggregation. Key must match the keys used in `fields` above, or the frontend can't look
        // up the values by column — FieldId alone is NOT sufficient here (see aggAliasToKey above).
        var columns = new List<ReportColumnInfo>
        {
            new() { FieldId = groupByField.Fid ?? groupByField.Id, Key = groupKey, Name = string.IsNullOrWhiteSpace(groupByField.Label) ? groupByField.Name : groupByField.Label, TypeCode = groupByField.TypeCode },
            new() { FieldId = 0, Key = "0", Name = "Count", TypeCode = "Number" },
        };
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

    /// <summary>Swaps a raw grouped/series value for its resolved display name when one was
    /// looked up (User/MultiUser group-by or series fields in Summary/Chart reports) — returns
    /// the value unchanged for every other field type, or if the id wasn't in the lookup
    /// (e.g. a deleted user).</summary>
    private static object? ResolveGroupOrSeriesValue(object? rawValue, IReadOnlyDictionary<long, string>? names)
    {
        if (names is null || rawValue is null) return rawValue;
        return long.TryParse(rawValue.ToString(), out var id) && names.TryGetValue(id, out var name) ? name : rawValue;
    }

    internal static async Task<IReadOnlyDictionary<long, string>> ResolveUserNamesAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<AppField> fields,
        IUserRepository userRepo,
        CancellationToken ct)
    {
        var hasUserFields = fields.Any(f =>
            f.TypeCode is "User" or "MultiUser" ||
            (f.IsSystem && f.PhysicalColumnName is "CreatedBy" or "ModifiedBy"));

        if (!hasUserFields) return new Dictionary<long, string>();

        var ids = new HashSet<long>();
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
}
