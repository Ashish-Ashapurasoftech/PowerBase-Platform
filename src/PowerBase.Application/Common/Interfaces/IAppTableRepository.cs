using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppTableRepository
{
    Task<AppTable> GetByIdAsync(long id, CancellationToken ct = default);
    Task<AppTable> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
    Task<IReadOnlyList<AppTable>> ListByAppAsync(long appId, CancellationToken ct = default);
    /// <summary>Slim, paged, searchable, sortable listing for the tables list UI — excludes Fields.
    /// When <paramref name="isShowInBar"/> is supplied, only tables matching it are returned (sidebar use case);
    /// when null, all tables are returned (full listing use case).</summary>
    Task<IReadOnlyList<AppTableListItemDto>> ListByAppPagedAsync(long appId, int page, int pageSize, string? search, string sortBy, bool sortDesc, bool? isShowInBar = null, CancellationToken ct = default);
    Task<int> CountByAppAsync(long appId, string? search, bool? isShowInBar = null, CancellationToken ct = default);
    Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppTable table, CancellationToken ct = default);
    Task UpdatePhysicalNameAsync(long id, string physicalTableName, CancellationToken ct = default);
    Task<int> UpdateAsync(Guid publicId, string name, string? singularLabel, string? pluralLabel, string? description, string? icon, long? defaultRecordPickerField1Id = null, long? defaultRecordPickerField2Id = null, long? defaultRecordPickerField3Id = null, bool? isShowInBar = null, CancellationToken ct = default);
    Task UpdateDefaultReportSettingsAsync(Guid publicId, string defaultReportSettings, CancellationToken ct = default);
    /// <summary>Sets the table's key field (null = reset to Record ID#, the default).</summary>
    Task SetKeyFieldAsync(long tableId, long? keyFieldId, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task IncrementRecordCountAsync(long id, CancellationToken ct = default);
    Task DecrementRecordCountAsync(long id, CancellationToken ct = default);
}
