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
}
