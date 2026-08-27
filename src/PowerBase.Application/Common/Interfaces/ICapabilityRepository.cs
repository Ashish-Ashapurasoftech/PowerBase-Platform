using PowerBase.Application.Capabilities.Dtos;

namespace PowerBase.Application.Common.Interfaces;

public interface ICapabilityRepository
{
    Task<IReadOnlyList<CapabilityDto>> GetAllActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RoleCapabilityDto>> GetRoleCapabilitiesAsync(Guid rolePublicId, CancellationToken ct = default);
    Task SaveRoleCapabilitiesAsync(Guid rolePublicId, IReadOnlyList<string> capabilityCodes, CancellationToken ct = default);
    Task UpdateRoleCapabilityAsync(Guid rolePublicId, string capabilityCode, bool enabled, CancellationToken ct = default);
}
