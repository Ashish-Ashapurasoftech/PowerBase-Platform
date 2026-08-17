using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IFieldTypeRepository
{
    Task<FieldType?> GetByCodeAsync(string code, CancellationToken ct = default);
    Task<int> GetIdByCodeAsync(string code, CancellationToken ct = default);
    /// <summary>Returns every supported field type configuration, in catalog order.</summary>
    Task<IReadOnlyList<FieldType>> ListAllAsync(CancellationToken ct = default);
}
