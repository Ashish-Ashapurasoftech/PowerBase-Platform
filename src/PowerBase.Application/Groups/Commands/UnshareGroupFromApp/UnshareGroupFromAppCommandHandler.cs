using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Groups.Commands.UnshareGroupFromApp;

public class UnshareGroupFromAppCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IAuditRepository _auditRepository;

    public UnshareGroupFromAppCommandHandler(
        IGroupRepository groupRepository,
        IAuditRepository auditRepository)
    {
        _groupRepository = groupRepository;
        _auditRepository = auditRepository;
    }

    public async Task<bool> HandleAsync(UnshareGroupFromAppCommand command, CancellationToken ct = default)
    {
        var existingGroup = await _groupRepository.GetByPublicIdAsync(command.GroupPublicId, ct);
        if (existingGroup == null)
            throw new KeyNotFoundException($"Group '{command.GroupPublicId}' not found.");

        var unshared = await _groupRepository.UnshareFromAppAsync(command.GroupPublicId, command.AppPublicId, ct);

        if (unshared)
        {
            await _auditRepository.LogActivityAsync(
                AuditActions.Updated,
                AuditEntityTypes.Group,
                command.GroupPublicId.ToString(),
                $"Unshared group '{existingGroup.Name}' from app {command.AppPublicId}",
                oldValues: JsonSerializer.Serialize(new { AppPublicId = command.AppPublicId }),
                ct: ct);
        }

        return unshared;
    }
}
