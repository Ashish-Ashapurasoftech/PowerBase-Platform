using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;

namespace PowerBase.Application.Groups.Queries.GetSharedApps;

public class GetSharedAppsQueryHandler
{
    private readonly IGroupRepository _groupRepository;

    public GetSharedAppsQueryHandler(IGroupRepository groupRepository)
    {
        _groupRepository = groupRepository;
    }

    public async Task<IEnumerable<SharedAppDto>> HandleAsync(GetSharedAppsQuery query, CancellationToken ct = default)
    {
        return await _groupRepository.GetSharedAppsAsync(query.GroupPublicId, ct);
    }
}
