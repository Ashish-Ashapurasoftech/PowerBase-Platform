using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;

namespace PowerBase.Application.Groups.Queries.ListGroupMembers;

public class ListGroupMembersQueryHandler
{
    private readonly IGroupRepository _groupRepository;

    public ListGroupMembersQueryHandler(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<(IEnumerable<GroupMemberDto> Items, int TotalCount)> HandleAsync(ListGroupMembersQuery query, CancellationToken ct = default)
    {
        return await _groupRepository.ListMembersAsync(query.GroupPublicId, query.Page, query.PageSize, ct);
    }
}
