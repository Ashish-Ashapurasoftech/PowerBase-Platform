using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Reports.Queries.ListReports;

public class ListReportsQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppRepository _appRepo;

    public ListReportsQueryHandler(IReportRepository reportRepo, IAppRepository appRepo)
    {
        _reportRepo = reportRepo;
        _appRepo = appRepo;
    }

    public async Task<IReadOnlyList<ReportDetailResult>> HandleAsync(ListReportsQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var reports = await _reportRepo.ListByAppAsync(appId, ct);

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
