using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Commands.RemoveGroupMember;

public class RemoveGroupMemberCommandHandler
{
    private readonly IGroupRepository _groupRepository;

    public RemoveGroupMemberCommandHandler(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<bool> HandleAsync(RemoveGroupMemberCommand command, CancellationToken ct = default)
    {
        var removed = await _groupRepository.RemoveMemberAsync(command.GroupPublicId, command.UserPublicId, ct);
        if (!removed)
            throw new KeyNotFoundException("Group member not found.");

        return true;
    }
}
