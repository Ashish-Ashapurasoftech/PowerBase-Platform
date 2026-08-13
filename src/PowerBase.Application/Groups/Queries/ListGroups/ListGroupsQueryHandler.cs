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

    public async Task<ListGroupsResult> HandleAsync(ListGroupsQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;

        var (items, total) = await _groupRepository.ListPagedAsync(query.Search, page, pageSize, ct);

        return new ListGroupsResult
        {
            Items = items.ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }
}
