using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Groups.Commands.AssignGroupRole;

public class AssignGroupRoleCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IAppRoleRepository _appRoleRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepository;

    public AssignGroupRoleCommandHandler(
        IGroupRepository groupRepository, 
        IAppRoleRepository appRoleRepository, 
        IQueryContext queryContext,
        IAuditRepository auditRepository)
    {
        _groupRepository = groupRepository;
        _appRoleRepository = appRoleRepository;
        _queryContext = queryContext;
        _auditRepository = auditRepository;
    }

    public async Task<bool> HandleAsync(AssignGroupRoleCommand command, CancellationToken ct = default)
    {
        long? appRoleId = null;
        string? appRoleName = null;
        if (command.AppRolePublicId.HasValue)
        {
            var role = await _appRoleRepository.GetByPublicIdAsync(command.AppRolePublicId.Value, ct);
            if (role == null)
                throw new KeyNotFoundException($"App role '{command.AppRolePublicId.Value}' not found.");
            appRoleId = role.Id;
            appRoleName = role.Name;
        }

        var group = await _groupRepository.GetByPublicIdAsync(command.GroupPublicId, ct);
        if (group == null)
            throw new KeyNotFoundException($"Group '{command.GroupPublicId}' not found.");

        var oldValues = new
        {
            AppRolePublicId = group.AppRolePublicId,
            AppRoleName = group.AppRoleName
        };

        var updated = await _groupRepository.UpdateAsync(
            command.GroupPublicId,
            group.Name,
            group.Description,
            appRoleId,
            _queryContext.UserId,
            ct);

        if (updated)
        {
            var newValues = new
            {
                AppRolePublicId = command.AppRolePublicId,
                AppRoleName = appRoleName
            };

            await _auditRepository.LogActivityAsync(
                AuditActions.RoleChanged,
                AuditEntityTypes.Group,
                command.GroupPublicId.ToString(),
                $"Group role changed: {group.Name} to role {appRoleName ?? "None"}",
                oldValues: JsonSerializer.Serialize(oldValues),
                newValues: JsonSerializer.Serialize(newValues),
                ct: ct);
        }

        return updated;
    }
}
