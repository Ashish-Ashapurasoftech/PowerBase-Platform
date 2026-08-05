using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;

namespace PowerBase.Application.Reports.Queries.ListReports;

public class ListReportsQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;

    public ListReportsQueryHandler(IReportRepository reportRepo, IAppRepository appRepo, IAppTableRepository tableRepo)
    {
        _reportRepo = reportRepo;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
    }

    public async Task<IReadOnlyList<ReportDetailResult>> HandleAsync(ListReportsQuery query, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var reports = await _reportRepo.ListByAppAsync(appId, ct);
        var tables = (await _tableRepo.ListByAppAsync(appId, ct)).ToDictionary(t => t.Id);

        var results = new List<ReportDetailResult>();
        foreach (var r in reports)
        {
            var def = JsonSerializer.Deserialize<ReportDefinition>(r.Definition) ?? new ReportDefinition();
            
            var visibleToRoleIds = new List<Guid>();
            if (r.Visibility == Domain.Enums.Visibility.SpecificRoles.ToString())
            {
                visibleToRoleIds = (await _reportRepo.GetReportRolePublicIdsAsync(r.Id, ct)).ToList();
            }

            tables.TryGetValue(r.AppTableId, out var table);

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
                TableId = table?.PublicId ?? Guid.Empty,
                TableName = table?.Name ?? string.Empty,
                CreatedOn = r.CreatedOn,
                VisibleToRoleIds = visibleToRoleIds
            });
        }
        
        return results;
    }
}
