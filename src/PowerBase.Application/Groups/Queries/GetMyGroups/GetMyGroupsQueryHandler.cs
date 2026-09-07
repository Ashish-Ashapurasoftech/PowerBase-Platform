using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;

namespace PowerBase.Application.Groups.Queries.GetMyGroups;

public class GetMyGroupsQueryHandler
{
    private readonly IGroupRepository _groupRepository;
    private readonly IQueryContext _queryContext;

    public GetMyGroupsQueryHandler(IGroupRepository groupRepository, IQueryContext queryContext)
    {
        _groupRepository = groupRepository;
        _queryContext = queryContext;
    }

    public async Task<IReadOnlyList<GroupDto>> HandleAsync(GetMyGroupsQuery query, CancellationToken ct = default)
    {
        var userId = query.UserId > 0 ? query.UserId : _queryContext.UserId;
        var items = await _groupRepository.GetMyGroupsAsync(userId, ct);
        return items.ToList();
    }
}
