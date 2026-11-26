using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;

namespace PowerBase.Application.Records.Queries.ListRecords;

public class PagedRecordResult
{
    public IReadOnlyList<Records.RecordResult> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListRecordsQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IUserRepository _userRepo;
    private readonly IFormulaProjector _formulaProjector;
    private readonly IRelationalProjector _relationalProjector;
    private readonly Compute.IMaterializedFieldState? _materialized;

    public ListRecordsQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IUserRepository userRepo,
        IFormulaProjector formulaProjector,
        IRelationalProjector relationalProjector,
        Compute.IMaterializedFieldState? materialized = null)
    {
        _materialized = materialized;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _userRepo = userRepo;
        _formulaProjector = formulaProjector;
        _relationalProjector = relationalProjector;
    }

    public async Task<PagedRecordResult> HandleAsync(ListRecordsQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.CanView)
            return new PagedRecordResult { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };

        var visibleFields = access.VisibleFields;

        // Merge the permission view-filter with an optional Report Link ad-hoc filter.
        var effectiveFilter = BuildEffectiveFilter(access.ViewFilter, query.FilterFid, query.FilterValue, table, fields);

        // Conditions on Formula/Lookup/Summary fields have no column, so the SQL builder skips them: evaluate
        // them in memory over every physically matching row, then page (same approach as RunReportQueryHandler).
        // Without this a View Custom Rule on a formula field would not restrict this endpoint at all.
        var formulaFids = fields
            .Where(f => f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();

        // A Ready stored Lookup / Summary / Formula (c_{fid}) and a Number formula SQL computes exactly are SQL-readable: a condition on
        // them runs in the query (every record, the database's work) instead of the batched in-memory pass below.
        var ready = _materialized is null ? new HashSet<long>() : await _materialized.ReadyFieldIdsAsync(ct);
        var sqlFormulas = SqlFormulaFields.Build(fields, isAppEncrypted: false, ready);   // reads only never-encrypted number columns
        formulaFids.ExceptWith(Compute.MaterializedReadFields.ReadyFids(fields, ready));
        formulaFids.ExceptWith(SqlFormulaFields.Fids(sqlFormulas));
        IReadOnlyList<PowerBase.Domain.Entities.AppField> SqlFields(IReadOnlyList<PowerBase.Domain.Entities.AppField> fs) =>
            SqlFormulaFields.Substitute(Compute.MaterializedReadFields.Substitute(fs, ready), sqlFormulas);
        var sqlAllFields = SqlFields(fields);

        if (FormulaFilterSorter.TreeContainsFormulaField(effectiveFilter, formulaFids))
        {
            var (physicalFilter, formulaFilter) = FormulaFilterSorter.SplitFilterTree(effectiveFilter, formulaFids);
            // Every physically matching row, in Id batches (one batch in memory at a time, no row limit): the matches are counted
            // and only the requested page's rows are kept. Project every field: a rule's formula may itself be hidden from the
            // role, or depend on hidden fields.
            var skipCount = (page - 1) * pageSize;
            var matchCount = 0;
            var pagePairs = new List<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)>();
            await foreach (var matches in MatchStream.MatchesAsync(_recordRepo, _relationalProjector, _formulaProjector, table, fields,
                               sqlAllFields, fields, fields, physicalFilter, formulaFilter, access.RestrictToCreatedBy, ct))
            {
                foreach (var pair in matches)
                {
                    if (matchCount >= skipCount && pagePairs.Count < pageSize) pagePairs.Add(pair);
                    matchCount++;
                }
            }

            var pageUserNames = await RunReportQueryHandler.ResolveUserNamesAsync(
                pagePairs.Select(p => p.Row), visibleFields, _userRepo, ct, pagePairs.Select(p => p.Computed));
            return new PagedRecordResult
            {
                Items = pagePairs.Select(p => Records.RecordResult.FromRow(p.Row, visibleFields, pageUserNames, p.Computed)).ToList(),
                TotalCount = matchCount,
                Page = page,
                PageSize = pageSize,
            };
        }

        // The columns read stay those the role may see; the SQL stand-ins of calculated fields the filter uses are added so their
        // conditions resolve (a stand-in whose column is an expression is never SELECTed).
        var standInsUsed = sqlAllFields.Where(f => f.IsSystem && f.Fid.HasValue && formulaFidsAll(f) && !visibleFields.Any(v => v.Id == f.Id)).ToList();
        bool formulaFidsAll(PowerBase.Domain.Entities.AppField f) => ready.Contains(f.Id) || sqlFormulas.ContainsKey(f.Id);
        var listFields = standInsUsed.Count == 0 ? SqlFields(visibleFields) : [.. SqlFields(visibleFields), .. standInsUsed];
        var rows = await _recordRepo.ListAsync(
            table, listFields, page, pageSize,
            filterTree: effectiveFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
        var total = await _recordRepo.CountAsync(
            table, sqlAllFields, filterTree: effectiveFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

        var relational = Relationships.ReferenceOriginalFallback.Apply(visibleFields, rows,
            await _relationalProjector.ProjectAsync(table, visibleFields, rows, ct));
        var computed = _formulaProjector.Project(visibleFields, rows, relational, table);
        var userNames = await RunReportQueryHandler.ResolveUserNamesAsync(rows, visibleFields, _userRepo, ct, computed);

        return new PagedRecordResult
        {
            Items = rows.Select((r, i) => Records.RecordResult.FromRow(r, visibleFields, userNames, computed[i])).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    private static FilterGroup? BuildEffectiveFilter(FilterGroup? viewFilter, int? filterFid, string? filterValue, PowerBase.Domain.Entities.AppTable table, IReadOnlyList<PowerBase.Domain.Entities.AppField> fields)
    {
        if (filterFid is null || filterValue is null)
            return viewFilter;

        var effectiveFid = filterFid.Value;

        // If the query asks for Record ID# (3) but the table has a custom Key Field,
        // intercept it and search the custom Key Field instead to prevent SQL type errors
        // and return the correct lookup record.
        if (effectiveFid == 3 && table.KeyFieldId.HasValue)
        {
            var keyField = fields.FirstOrDefault(f => f.Id == table.KeyFieldId.Value);
            if (keyField?.Fid != null)
            {
                effectiveFid = keyField.Fid.Value;
            }
        }

        var adHoc = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = effectiveFid, Operator = "eq", Value = filterValue } }]
        };

        if (viewFilter is null)
            return adHoc;

        return new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Group = viewFilter }, new FilterNode { Group = adHoc }]
        };
    }
}
