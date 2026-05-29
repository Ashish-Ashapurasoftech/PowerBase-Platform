using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Reports.Queries.ListReportsByTable;

public class ListReportsByTableQueryHandler
{
    private readonly IReportRepository _reportRepo;

    public ListReportsByTableQueryHandler(IReportRepository reportRepo)
    {
        _reportRepo = reportRepo;
    }

    public async Task<IReadOnlyList<ReportDetailResult>> HandleAsync(
        ListReportsByTableQuery query, CancellationToken ct = default)
    {
        var reports = await _reportRepo.ListByTableAsync(query.TablePublicId, ct);

        return reports.Select(r =>
        {
            var def = JsonSerializer.Deserialize<ReportDefinition>(r.Definition) ?? new ReportDefinition();
            return new ReportDetailResult
            {
                Id = r.PublicId,
                Name = r.Name,
                Description = r.Description,
                ReportType = r.ReportType,
                Visibility = r.Visibility,
                Definition = def,
                IsDefault = r.IsDefault,
                DisplayOrder = r.DisplayOrder,
                ViewEditFormId = r.ViewEditFormPublicId,
                CreatedOn = r.CreatedOn,
            };
        }).ToList();
    }
}
