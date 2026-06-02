using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Queries.GetReport;

public class GetReportQueryHandler
{
    private readonly IReportRepository _reportRepo;

    public GetReportQueryHandler(IReportRepository reportRepo)
    {
        _reportRepo = reportRepo;
    }

    public async Task<ReportDetailResult> HandleAsync(GetReportQuery query, CancellationToken ct = default)
    {
        var report = await _reportRepo.GetVisibleReportAsync(query.PublicId, ct)
            ?? throw new NotFoundException("Report", query.PublicId);
        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();

        var visibleToRoleIds = new List<Guid>();
        if (report.Visibility == Domain.Enums.Visibility.SpecificRoles.ToString())
        {
            visibleToRoleIds = (await _reportRepo.GetReportRolePublicIdsAsync(report.Id, ct)).ToList();
        }

        return new ReportDetailResult
        {
            Id = report.PublicId,
            Name = report.Name,
            Description = report.Description,
            ReportType = report.ReportType,
            Visibility = report.Visibility,
            Definition = definition,
            IsDefault = report.IsDefault,
            DisplayOrder = report.DisplayOrder,
            ViewEditFormId = report.ViewEditFormPublicId,
            CreatedOn = report.CreatedOn,
            VisibleToRoleIds = visibleToRoleIds
        };
    }
}
