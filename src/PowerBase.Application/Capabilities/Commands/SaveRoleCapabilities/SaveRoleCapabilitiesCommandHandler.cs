using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Capabilities.Commands.SaveRoleCapabilities;

public class SaveRoleCapabilitiesCommandHandler
{
    private readonly ICapabilityRepository _capabilityRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAuditRepository _auditRepo;

    public SaveRoleCapabilitiesCommandHandler(
        ICapabilityRepository capabilityRepo,
        IAppRoleRepository appRoleRepo,
        IAuditRepository auditRepo)
    {
        _capabilityRepo = capabilityRepo;
        _appRoleRepo = appRoleRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(SaveRoleCapabilitiesCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("AppRole", command.RolePublicId);

        await _capabilityRepo.SaveRoleCapabilitiesAsync(command.RolePublicId, command.Capabilities, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated,
            AuditEntityTypes.AppRole,
            role.Id.ToString(),
            $"Updated builder capabilities for role: {role.Name}",
            appId: role.AppId,
            ct: ct);
    }
}
