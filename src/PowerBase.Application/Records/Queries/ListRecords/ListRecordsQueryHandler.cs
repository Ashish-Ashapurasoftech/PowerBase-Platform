using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
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

    public ListRecordsQueryHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IUserRepository userRepo,
        IFormulaProjector formulaProjector)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _userRepo = userRepo;
        _formulaProjector = formulaProjector;
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
        var rows = await _recordRepo.ListAsync(
            table, visibleFields, page, pageSize,
            filterTree: access.ViewFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
        var total = await _recordRepo.CountAsync(
            table, fields, filterTree: access.ViewFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

        var userNames = await RunReportQueryHandler.ResolveUserNamesAsync(rows, visibleFields, _userRepo, ct);
        var computed = _formulaProjector.Project(visibleFields, rows);

        return new PagedRecordResult
        {
            Items = rows.Select((r, i) => Records.RecordResult.FromRow(r, visibleFields, userNames, computed[i])).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }
}
