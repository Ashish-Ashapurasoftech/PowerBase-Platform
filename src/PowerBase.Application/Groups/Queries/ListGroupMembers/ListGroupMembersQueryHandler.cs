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

    public async Task<ListGroupMembersResult> HandleAsync(ListGroupMembersQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 10000 ? 20 : query.PageSize;

        var (items, total) = await _groupRepository.ListMembersAsync(query.GroupPublicId, page, pageSize, ct);

        return new ListGroupMembersResult
        {
            Items = items.ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }
}
