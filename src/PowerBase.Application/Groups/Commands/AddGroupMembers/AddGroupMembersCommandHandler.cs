using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Groups.Commands.AddGroupMembers;

public class AddGroupMembersCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepository;

    public AddGroupMembersCommandHandler(
        IGroupRepository groupRepository, 
        IQueryContext queryContext,
        IAuditRepository auditRepository)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
        _auditRepository = auditRepository;
    }

    public async Task<int> HandleAsync(AddGroupMembersCommand command, CancellationToken ct = default)
    {
        if (command.UserPublicIds == null || !command.UserPublicIds.Any())
            return 0;

        var existingGroup = await _groupRepository.GetByPublicIdAsync(command.GroupPublicId, ct);
        if (existingGroup == null)
            throw new KeyNotFoundException($"Group '{command.GroupPublicId}' not found.");

        var addedCount = await _groupRepository.AddMembersAsync(
            command.GroupPublicId,
            command.UserPublicIds,
            _queryContext.UserId,
            ct);

        if (addedCount > 0)
        {
            await _auditRepository.LogActivityAsync(
                AuditActions.Updated,
                AuditEntityTypes.Group,
                command.GroupPublicId.ToString(),
                $"Added {addedCount} member(s) to group '{existingGroup.Name}'",
                newValues: JsonSerializer.Serialize(new { UserPublicIds = command.UserPublicIds }),
                ct: ct);
        }

        return addedCount;
    }
}
