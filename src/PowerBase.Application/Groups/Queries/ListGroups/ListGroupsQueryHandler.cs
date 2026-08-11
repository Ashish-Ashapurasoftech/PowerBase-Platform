using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;

namespace PowerBase.Application.Groups.Queries.ListGroups;

public class ListGroupsQueryHandler
{
    private readonly IGroupRepository _groupRepository;

    public ListGroupsQueryHandler(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<(IEnumerable<GroupDto> Items, int TotalCount)> HandleAsync(ListGroupsQuery query, CancellationToken ct = default)
    {
        return await _groupRepository.ListPagedAsync(query.Search, query.Page, query.PageSize, ct);
    }
}
