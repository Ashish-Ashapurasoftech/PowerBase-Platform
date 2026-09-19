using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;

namespace PowerBase.Application.Groups.Queries.ListGroups;

public class ListGroupsQueryHandler
{
    private static readonly HashSet<string> AllowedSortFields =
        new(StringComparer.OrdinalIgnoreCase) { "name", "description", "memberCount", "createdOn" };

    private readonly IGroupRepository _groupRepository;

    public ListGroupsQueryHandler(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<ListGroupsResult> HandleAsync(ListGroupsQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;
        var sortBy = !string.IsNullOrWhiteSpace(query.SortBy) && AllowedSortFields.Contains(query.SortBy)
            ? query.SortBy
            : "name";

        var (items, total) = await _groupRepository.ListPagedAsync(query.Search, page, pageSize, sortBy, query.SortDesc, ct);

        return new ListGroupsResult
        {
            Items = items.ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }
}
