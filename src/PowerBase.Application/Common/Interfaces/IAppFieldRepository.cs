using PowerBase.Domain.Entities;
using PowerBase.Application.Fields.Queries.GetFieldUsage;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppFieldRepository
{
    Task<AppField> GetByIdInTableAsync(long fieldId, long tableId, CancellationToken ct = default);
    Task<IReadOnlyList<AppField>> ListByTableAsync(long tableId, CancellationToken ct = default);
    Task<bool> NameExistsInTableAsync(long tableId, string name, CancellationToken ct = default);
    Task<AppField?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppField field, CancellationToken ct = default);
    Task UpdatePhysicalColumnNameAsync(long id, string physicalColumnName, CancellationToken ct = default);
    /// <summary>Lightweight settings-only update (used when wiring relationship metadata after field creation).</summary>
    Task UpdateSettingsAsync(long id, string? settings, CancellationToken ct = default);

    /// <summary>Changes a field's type (and settings/required) in place, keeping its Fid and physical column.
    /// Used when converting an existing physical field into a relationship Reference field.</summary>
    Task UpdateFieldTypeAsync(long id, long fieldTypeId, string? settings, bool isRequired, CancellationToken ct = default);
    Task<int> GetNextFidAsync(long tableId, CancellationToken ct = default);
    Task<AppField?> GetByFidInTableAsync(long tableId, int fid, CancellationToken ct = default);
    Task<int> UpdateAsync(Guid publicId, long tableId, string name, string? label, string? description,
        bool isRequired, string? defaultValue, bool isSearchable, bool isSortable,
        bool isFilterable, bool isReportable, bool isAuditable, bool isUnique, string? settings, CancellationToken ct = default);
    Task<int> DeleteAsync(Guid publicId, long tableId, CancellationToken ct = default);
    Task<int> BulkDeleteAsync(IEnumerable<Guid> publicIds, long tableId, CancellationToken ct = default);
    Task<FieldUsageDto> GetFieldUsageAsync(long tableId, long fieldId, int fid, long appId, CancellationToken ct = default);
}
