using PowerBase.Application.Capabilities.Dtos;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Capabilities.Queries.ListCapabilities;

public class ListCapabilitiesQueryHandler
{
    private readonly ICapabilityRepository _capabilityRepo;

    public ListCapabilitiesQueryHandler(ICapabilityRepository capabilityRepo)
    {
        _capabilityRepo = capabilityRepo;
    }

    public async Task<IReadOnlyList<CapabilityDto>> HandleAsync(ListCapabilitiesQuery query, CancellationToken ct = default)
    {
        return await _capabilityRepo.GetAllActiveAsync(ct);
    }
}
