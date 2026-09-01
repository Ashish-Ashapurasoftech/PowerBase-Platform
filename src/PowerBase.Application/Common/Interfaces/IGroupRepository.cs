using PowerBase.Application.Groups.Common;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IGroupRepository
{
    // CRUD
    Task<Group> CreateAsync(Group group, CancellationToken ct = default);
    Task<GroupDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<(IEnumerable<GroupDto> Items, int TotalCount)> ListPagedAsync(string? search, int page, int pageSize, CancellationToken ct = default);
    Task<bool> UpdateAsync(Guid publicId, string name, string? description, long modifiedBy, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid publicId, long deletedBy, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(string name, Guid? excludePublicId, CancellationToken ct = default);

    // Members
    Task<int> AddMembersAsync(Guid groupPublicId, IEnumerable<Guid> userPublicIds, long addedBy, CancellationToken ct = default);
    Task<bool> RemoveMemberAsync(Guid groupPublicId, Guid userPublicId, CancellationToken ct = default);
    Task<(IEnumerable<GroupMemberDto> Items, int TotalCount)> ListMembersAsync(Guid groupPublicId, int page, int pageSize, CancellationToken ct = default);
    Task<IEnumerable<GroupDto>> GetMyGroupsAsync(long userId, CancellationToken ct = default);

    // Sharing
    Task<bool> ShareWithAppsAsync(Guid groupPublicId, IEnumerable<Guid> appPublicIds, long createdBy, Guid? appRolePublicId = null, CancellationToken ct = default);
    Task<bool> UnshareFromAppAsync(Guid groupPublicId, Guid appPublicId, CancellationToken ct = default);
    Task<IEnumerable<SharedAppDto>> GetSharedAppsAsync(Guid groupPublicId, CancellationToken ct = default);
}
