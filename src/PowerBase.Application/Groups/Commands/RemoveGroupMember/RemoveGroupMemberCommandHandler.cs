using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Groups.Commands.RemoveGroupMember;

public class RemoveGroupMemberCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IAuditRepository _auditRepository;

    public RemoveGroupMemberCommandHandler(
        IGroupRepository groupRepository,
        IAuditRepository auditRepository)
    {
        _groupRepository = groupRepository;
        _auditRepository = auditRepository;
    }

    public async Task<bool> HandleAsync(RemoveGroupMemberCommand command, CancellationToken ct = default)
    {
        var existingGroup = await _groupRepository.GetByPublicIdAsync(command.GroupPublicId, ct);
        if (existingGroup == null)
            throw new KeyNotFoundException($"Group '{command.GroupPublicId}' not found.");

        var removed = await _groupRepository.RemoveMemberAsync(command.GroupPublicId, command.UserPublicId, ct);
        if (!removed)
            throw new KeyNotFoundException("Group member not found.");

        await _auditRepository.LogActivityAsync(
            AuditActions.Updated,
            AuditEntityTypes.Group,
            command.GroupPublicId.ToString(),
            $"Removed member {command.UserPublicId} from group '{existingGroup.Name}'",
            oldValues: JsonSerializer.Serialize(new { UserPublicId = command.UserPublicId }),
            ct: ct);

        return true;
    }
}
