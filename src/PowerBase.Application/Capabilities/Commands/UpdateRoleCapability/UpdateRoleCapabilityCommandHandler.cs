using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Capabilities.Commands.UpdateRoleCapability;

public class UpdateRoleCapabilityCommandHandler
{
    private readonly ICapabilityRepository _capabilityRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAuditRepository _auditRepo;

    public UpdateRoleCapabilityCommandHandler(
        ICapabilityRepository capabilityRepo,
        IAppRoleRepository appRoleRepo,
        IAuditRepository auditRepo)
    {
        _capabilityRepo = capabilityRepo;
        _appRoleRepo = appRoleRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateRoleCapabilityCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("AppRole", command.RolePublicId);

        await _capabilityRepo.UpdateRoleCapabilityAsync(command.RolePublicId, command.CapabilityCode, command.Enabled, ct);

        var actionText = command.Enabled ? "Enabled" : "Disabled";
        await _auditRepo.LogActivityAsync(
            AuditActions.Updated,
            AuditEntityTypes.AppRole,
            role.Id.ToString(),
            $"{actionText} builder capability '{command.CapabilityCode}' for role: {role.Name}",
            appId: role.AppId,
            ct: ct);
    }
}
