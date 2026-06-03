using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateAppRole;

public class UpdateAppRoleCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;

    public UpdateAppRoleCommandHandler(IAppRoleRepository appRoleRepo, IAppAccessService appAccessService, IAuditRepository auditRepo)
    {
        _appRoleRepo = appRoleRepo;
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateAppRoleCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct);
        if (role is null)
            throw new NotFoundException("AppRole", command.RolePublicId);

        await _appRoleRepo.SetPermissionsAsync(role.Id, command.Permissions, null, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(), $"App role permissions modified: {role.Name}", appId: role.AppId, ct: ct);
    }
}
