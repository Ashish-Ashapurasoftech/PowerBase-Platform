using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Commands.AddGroupMembers;

public class AddGroupMembersCommandHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;

    public AddGroupMembersCommandHandler(IGroupRepository groupRepository, IQueryContext queryContext)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
    }

    public async Task<int> HandleAsync(AddGroupMembersCommand command, CancellationToken ct = default)
    {
        if (command.UserPublicIds == null || !command.UserPublicIds.Any())
            return 0;

        return await _groupRepository.AddMembersAsync(
            command.GroupPublicId,
            command.UserPublicIds,
            _queryContext.UserId,
            ct);
    }
}
