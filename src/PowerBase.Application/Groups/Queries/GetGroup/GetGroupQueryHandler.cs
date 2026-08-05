using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;

namespace PowerBase.Application.Groups.Queries.GetGroup;

public class GetGroupQueryHandler
{
    private readonly IGroupRepository _groupRepository;

    public GetGroupQueryHandler(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<GroupDto> HandleAsync(GetGroupQuery query, CancellationToken ct = default)
    {
        var group = await _groupRepository.GetByPublicIdAsync(query.PublicId, ct);
        if (group is null)
            throw new KeyNotFoundException($"Group '{query.PublicId}' not found.");

        return group;
    }
}
