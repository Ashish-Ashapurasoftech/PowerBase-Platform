using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;
using PowerBase.Application.Fields.Queries.GetFieldUsage;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppFieldRepository
{
    Task<AppField> GetByIdInTableAsync(long fieldId, long tableId, CancellationToken ct = default);
    Task<IReadOnlyList<AppField>> ListByTableAsync(long tableId, CancellationToken ct = default);
    /// <summary>Slim, paged, searchable (by Label), sortable, filterable listing for the fields grid.
    /// <paramref name="filter"/> accepts either a dropdown value (e.g. "System Fields", "Required Fields")
    /// or a category name (Text/Numeric/Date/Other/User/Formula/Relationship/Action); null/"All Fields"
    /// returns everything.</summary>
    Task<IReadOnlyList<AppFieldListItemDto>> ListByTablePagedAsync(
        long tableId, int page, int pageSize, string? search, string sortBy, bool sortDesc, string? filter, CancellationToken ct = default);
    Task<int> CountByTableAsync(long tableId, string? search, string? filter, CancellationToken ct = default);
    /// <summary>Internal collision check used only by IFieldNameResolver when generating a new Name. Not a user-facing duplicate check.</summary>
    Task<bool> NameExistsInTableAsync(long tableId, string name, CancellationToken ct = default);
    /// <summary>User-facing duplicate check — Label is the value users edit and must be unique per table.</summary>
    Task<bool> LabelExistsInTableAsync(long tableId, string label, long? excludeFieldId = null, CancellationToken ct = default);
    Task<AppField?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppField field, CancellationToken ct = default);
    Task UpdatePhysicalColumnNameAsync(long id, string physicalColumnName, CancellationToken ct = default);
    /// <summary>Lightweight settings-only update (used when wiring relationship metadata after field creation).</summary>
    Task UpdateSettingsAsync(long id, string? settings, CancellationToken ct = default);

    /// <summary>Changes a field's type (and settings/required) in place, keeping its Fid and physical column.
    /// Used when converting an existing physical field into a relationship Reference field.</summary>
    Task UpdateFieldTypeAsync(long id, long fieldTypeId, string? settings, bool isRequired, CancellationToken ct = default);

    /// <summary>
    /// Reverts a Reference field that was converted from an existing Number field back to
    /// plain Number type (clears relationship-specific settings, restores original type code).
    /// </summary>
    Task RevertToNumberFieldAsync(long fieldId, CancellationToken ct = default);

    Task<int> GetNextFidAsync(long tableId, CancellationToken ct = default);
    Task<AppField?> GetByFidInTableAsync(long tableId, int fid, CancellationToken ct = default);
    /// <summary>Updates a field's editable properties. Name is intentionally not a parameter — it is
    /// generated once at creation and immutable thereafter (stable third-party API identifier).</summary>
    Task<int> UpdateAsync(Guid publicId, long tableId, string? label, string? description,
        bool isRequired, string? defaultValue, bool isSearchable, bool isSortable,
        bool isFilterable, bool isReportable, bool isAuditable, bool isUnique, bool isEncrypted, string? settings, CancellationToken ct = default);
    Task<int> DeleteAsync(Guid publicId, long tableId, CancellationToken ct = default);
    Task<int> BulkDeleteAsync(IEnumerable<Guid> publicIds, long tableId, CancellationToken ct = default);
    Task<FieldUsageDto> GetFieldUsageAsync(long tableId, long fieldId, int fid, long appId, CancellationToken ct = default);
}
