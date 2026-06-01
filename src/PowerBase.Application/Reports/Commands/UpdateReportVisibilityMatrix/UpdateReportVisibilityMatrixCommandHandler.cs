using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.UpdateReportVisibilityMatrix;

public class UpdateReportVisibilityMatrixCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAppAccessService _appAccessService;

    public UpdateReportVisibilityMatrixCommandHandler(
        IReportRepository reportRepo,
        IAppRoleRepository appRoleRepo,
        IAppRepository appRepo,
        IAppAccessService appAccessService)
    {
        _reportRepo = reportRepo;
        _appRoleRepo = appRoleRepo;
        _appRepo = appRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(UpdateReportVisibilityMatrixCommand command, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        await _appAccessService.RequirePermissionByAppIdAsync(app.Id, PowerBase.Domain.Constants.PermissionCodes.RolesManage, ct);

        // Fetch all roles for mapping
        var roles = await _appRoleRepo.ListDetailsByAppIdAsync(app.Id, ct);
        var roleMap = roles.ToDictionary(r => r.PublicId, r => r.Id);

        foreach (var update in command.Updates)
        {
            var report = await _reportRepo.GetByPublicIdAsync(update.ReportPublicId, ct);
            
            // Only update visibility if it actually changed or roles changed
            // Actually, we can just call UpdateAsync for the report's properties.
            // But we don't want to overwrite Name/Description/Definition.
            // Wait, UpdateAsync requires Name, Description, Definition!
            // I should just update the report's visibility and roles directly, or use UpdateAsync by passing existing values.
            
            await _reportRepo.UpdateAsync(
                report.PublicId,
                report.Name,
                report.Description,
                update.Visibility,
                report.Definition,
                ct);

            var reportRolesToSave = new System.Collections.Generic.List<long>();
            if (update.Visibility == Domain.Enums.Visibility.SpecificRoles.ToString() || update.Visibility == Domain.Enums.Visibility.MyRole.ToString())
            {
                foreach (var rolePubId in update.VisibleToRoleIds)
                {
                    if (roleMap.TryGetValue(rolePubId, out var roleId))
                    {
                        reportRolesToSave.Add(roleId);
                    }
                }
            }

            await _reportRepo.SetReportRolesAsync(report.Id, reportRolesToSave, ct);
        }
    }
}
