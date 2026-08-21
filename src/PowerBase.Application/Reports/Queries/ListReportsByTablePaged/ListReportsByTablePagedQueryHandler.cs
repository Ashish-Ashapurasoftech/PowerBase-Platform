using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Reports.Queries.ListReportsByTablePaged;

public class ListReportsByTablePagedResult
{
    public IReadOnlyList<ReportListItemDto> Items { get; init; } = Array.Empty<ReportListItemDto>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class ListReportsByTablePagedQueryHandler
{
    private static readonly HashSet<string> AllowedSortFields =
        new(StringComparer.OrdinalIgnoreCase) { "name", "reportType", "visibility", "isDefault", "createdOn" };

    private readonly IReportRepository _reportRepo;

    public ListReportsByTablePagedQueryHandler(IReportRepository reportRepo) => _reportRepo = reportRepo;

    public async Task<ListReportsByTablePagedResult> HandleAsync(ListReportsByTablePagedQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;
        var sortBy = AllowedSortFields.Contains(query.SortBy) ? query.SortBy : "name";

        var items = await _reportRepo.ListByTablePagedAsync(query.TablePublicId, page, pageSize, query.Search, sortBy, query.SortDesc, ct);
        var total = await _reportRepo.CountByTableAsync(query.TablePublicId, query.Search, ct);

        return new ListReportsByTablePagedResult { Items = items, Total = total, Page = page, PageSize = pageSize };
    }
}
