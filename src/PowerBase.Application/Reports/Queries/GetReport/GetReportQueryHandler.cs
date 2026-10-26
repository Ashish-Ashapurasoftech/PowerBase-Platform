using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Queries.GetReport;

public class GetReportQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppFieldRepository? _fieldRepo;

    public GetReportQueryHandler(IReportRepository reportRepo, IAppFieldRepository? fieldRepo = null)
    {
        _reportRepo = reportRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task<ReportDetailResult> HandleAsync(GetReportQuery query, CancellationToken ct = default)
    {
        var report = await _reportRepo.GetVisibleReportAsync(query.PublicId, ct)
            ?? throw new NotFoundException("Report", query.PublicId);
        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();
        // The builder shows what actually runs: totals/grouping that no longer fit a field whose type was
        // changed are left out (Change Type), so re-saving the report doesn't fail validation.
        if (_fieldRepo is not null)
            PowerBase.Application.Fields.TypeConversion.ReportDefinitionTypeSanitizer.Apply(
                definition, await _fieldRepo.ListByTableAsync(report.AppTableId, ct));

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
