using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Enums;

namespace PowerBase.Application.Reports.Queries.GetRolesReportsMatrix;

public class GetRolesReportsMatrixQueryHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppTableRepository _appTableRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppAccessService _appAccessService;

    public GetRolesReportsMatrixQueryHandler(
        IAppRoleRepository appRoleRepo,
        IAppTableRepository appTableRepo,
        IReportRepository reportRepo,
        IAppRepository appRepo,
        IAppAccessService appAccessService)
    {
        _appRoleRepo = appRoleRepo;
        _appTableRepo = appTableRepo;
        _reportRepo = reportRepo;
        _appRepo = appRepo;
        _appAccessService = appAccessService;
    }

    public async Task<RolesReportsMatrixResult> HandleAsync(GetRolesReportsMatrixQuery query, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(query.AppPublicId, ct);
        await _appAccessService.RequirePermissionByAppIdAsync(app.Id, PowerBase.Domain.Constants.PermissionCodes.RolesManage, ct);

        var roles = await _appRoleRepo.ListDetailsByAppIdAsync(app.Id, ct);
        var tables = await _appTableRepo.ListByAppAsync(app.Id, ct);
        var reports = await _reportRepo.ListAllByAppAsync(app.Id, ct);
        var roleMappings = await _reportRepo.GetAppRoleReportsMapAsync(app.Id, ct);

        var roleMap = roles.ToDictionary(r => r.Id, r => r.PublicId);

        var result = new RolesReportsMatrixResult
        {
            Roles = roles.Select(r => new MatrixRole { PublicId = r.PublicId, Name = r.Name }).ToList(),
            Tables = tables.Select(t => new MatrixTable
            {
                PublicId = t.PublicId,
                Name = t.Name,
                Reports = reports.Where(r => r.AppTableId == t.Id).Select(r =>
                {
                    var reportRoles = roleMappings.TryGetValue(r.Id, out var mappedRoleIds)
                        ? mappedRoleIds.Select(id => roleMap.GetValueOrDefault(id)).Where(g => g != Guid.Empty).ToList()
                        : new List<Guid>();

                    return new MatrixReport
                    {
                        PublicId = r.PublicId,
                        Name = r.Name,
                        Visibility = r.Visibility,
                        VisibleToRoleIds = reportRoles
                    };
                }).ToList()
            }).Where(t => t.Reports.Any()).ToList() // Only return tables that have reports
        };

        return result;
    }
}
