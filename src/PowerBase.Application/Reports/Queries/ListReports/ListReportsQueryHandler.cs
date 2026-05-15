using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Reports.Queries.ListReports;

public class ListReportsQueryHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IReportRepository _reportRepo;

    public ListReportsQueryHandler(IAppRepository appRepo, IReportRepository reportRepo)
    {
        _appRepo = appRepo;
        _reportRepo = reportRepo;
    }

    public async Task<IReadOnlyList<ReportDetailResult>> HandleAsync(ListReportsQuery query, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(query.AppPublicId, ct);
        var reports = await _reportRepo.ListByAppAsync(app.Id, ct);

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
                CreatedOn = r.CreatedOn,
            };
        }).ToList();
    }
}
