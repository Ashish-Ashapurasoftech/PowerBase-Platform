using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Groups.Queries.GetSharedApps;

public class GetSharedAppsQueryHandler
{
    private readonly IGroupRepository _groupRepository;

    public GetSharedAppsQueryHandler(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<IEnumerable<Guid>> HandleAsync(GetSharedAppsQuery query, CancellationToken ct = default)
    {
        return await _groupRepository.GetSharedAppPublicIdsAsync(query.GroupPublicId, ct);
    }
}
