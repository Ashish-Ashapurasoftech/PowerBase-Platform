using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppFieldRepository
{
    Task<AppField> GetByIdInTableAsync(long fieldId, long tableId, CancellationToken ct = default);
    Task<IReadOnlyList<AppField>> ListByTableAsync(long tableId, CancellationToken ct = default);
    Task<bool> NameExistsInTableAsync(long tableId, string name, CancellationToken ct = default);
    Task<AppField?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppField field, CancellationToken ct = default);
    Task UpdatePhysicalColumnNameAsync(long id, string physicalColumnName, CancellationToken ct = default);
    Task<int> UpdateAsync(Guid publicId, long tableId, string name, string? label, string? description, CancellationToken ct = default);
    Task<int> DeleteAsync(Guid publicId, long tableId, CancellationToken ct = default);
}
