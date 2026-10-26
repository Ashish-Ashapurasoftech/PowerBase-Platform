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

    public ListRecordsQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IUserRepository userRepo,
        IFormulaProjector formulaProjector,
        IRelationalProjector relationalProjector)
    {
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
        if (FormulaFilterSorter.TreeContainsFormulaField(effectiveFilter, formulaFids))
        {
            var (physicalFilter, formulaFilter) = FormulaFilterSorter.SplitFilterTree(effectiveFilter, formulaFids);
            var allRows = await _recordRepo.ListAllMatchingAsync(
                table, fields, physicalFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
            // Project every field: a rule's formula may itself be hidden from the role, or depend on hidden fields.
            var allRelational = Relationships.ReferenceOriginalFallback.Apply(fields, allRows,
                await _relationalProjector.ProjectAsync(table, fields, allRows, ct));
            var allComputed = _formulaProjector.Project(fields, allRows, allRelational, table);
            var pairs = allRows.Zip(allComputed, (r, c) => (Row: r, Computed: c)).ToList();
            if (formulaFilter is not null)
                pairs = FormulaFilterSorter.ApplyFormulaFilters(pairs, formulaFilter, fields);

            var pagePairs = pairs.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var pageUserNames = await RunReportQueryHandler.ResolveUserNamesAsync(
                pagePairs.Select(p => p.Row), visibleFields, _userRepo, ct, pagePairs.Select(p => p.Computed));
            return new PagedRecordResult
            {
                Items = pagePairs.Select(p => Records.RecordResult.FromRow(p.Row, visibleFields, pageUserNames, p.Computed)).ToList(),
                TotalCount = pairs.Count,
                Page = page,
                PageSize = pageSize,
            };
        }

        var rows = await _recordRepo.ListAsync(
            table, visibleFields, page, pageSize,
            filterTree: effectiveFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
        var total = await _recordRepo.CountAsync(
            table, fields, filterTree: effectiveFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

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
