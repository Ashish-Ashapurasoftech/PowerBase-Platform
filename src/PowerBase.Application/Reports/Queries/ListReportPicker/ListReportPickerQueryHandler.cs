using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Reports.Queries.ListReportPicker;

public record ListReportPickerQuery(Guid TablePublicId);

public class ListReportPickerQueryHandler
{
    private readonly IReportRepository _reportRepo;

    public ListReportPickerQueryHandler(IReportRepository reportRepo) => _reportRepo = reportRepo;

    public Task<IReadOnlyList<ReportPickerItemDto>> HandleAsync(ListReportPickerQuery query, CancellationToken ct = default)
        => _reportRepo.ListPickerByTableAsync(query.TablePublicId, ct);
}
