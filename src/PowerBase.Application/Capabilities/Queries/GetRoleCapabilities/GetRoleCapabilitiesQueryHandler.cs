using PowerBase.Application.Capabilities.Dtos;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Capabilities.Queries.GetRoleCapabilities;

public class GetRoleCapabilitiesQueryHandler
{
    private readonly ICapabilityRepository _capabilityRepo;

    public GetRoleCapabilitiesQueryHandler(ICapabilityRepository capabilityRepo)
    {
        _capabilityRepo = capabilityRepo;
    }

    public async Task<IReadOnlyList<RoleCapabilityDto>> HandleAsync(GetRoleCapabilitiesQuery query, CancellationToken ct = default)
    {
        return await _capabilityRepo.GetRoleCapabilitiesAsync(query.RolePublicId, ct);
    }
}
