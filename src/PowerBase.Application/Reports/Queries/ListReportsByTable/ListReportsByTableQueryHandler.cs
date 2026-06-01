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

        var results = new List<ReportDetailResult>();
        foreach (var r in reports)
        {
            var def = JsonSerializer.Deserialize<ReportDefinition>(r.Definition) ?? new ReportDefinition();
            
            var visibleToRoleIds = new List<Guid>();
            if (r.Visibility == Domain.Enums.Visibility.SpecificRoles.ToString())
            {
                visibleToRoleIds = (await _reportRepo.GetReportRolePublicIdsAsync(r.Id, ct)).ToList();
            }

            results.Add(new ReportDetailResult
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
                VisibleToRoleIds = visibleToRoleIds
            });
        }
        
        return results;
    }
}
