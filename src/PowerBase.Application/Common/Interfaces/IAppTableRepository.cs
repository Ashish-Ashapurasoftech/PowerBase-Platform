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
    /// <summary>Slim, unpaginated listing for nav surfaces (sidebar, top nav, table switcher) — every
    /// table in the app, no search/sort/paging. Callers filter (isShowInBar) and search client-side.</summary>
    Task<IReadOnlyList<AppTableNavItemDto>> ListNavByAppAsync(long appId, CancellationToken ct = default);
    /// <summary>Just PublicId/Alias for every (non-deleted) table in the app — backs
    /// <see cref="PowerBase.Application.Formulas.AppTableAliasSchema"/>'s <c>[_DBID_*]</c>
    /// resolution. Deliberately doesn't reuse <see cref="ListByAppAsync"/>, which joins in every
    /// field of every table in the app — this needs none of that, and that join gets expensive on
    /// large tables (1000+ fields) for a query that only ever reads two columns off the parent
    /// table row.</summary>
    Task<IReadOnlyList<AppTableAliasDto>> ListAliasesByAppAsync(long appId, CancellationToken ct = default);
    Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default);
    /// <summary>Whether an existing (non-deleted) table in the app already has this Alias — used
    /// by table creation to dedupe generated <c>_DBID_*</c> aliases.</summary>
    Task<bool> AliasExistsInAppAsync(long appId, string alias, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppTable table, CancellationToken ct = default);
    Task UpdatePhysicalNameAsync(long id, string physicalTableName, CancellationToken ct = default);
    Task<int> UpdateAsync(Guid publicId, string name, string? singularLabel, string? pluralLabel, string? description, string? icon, long? defaultRecordPickerField1Id = null, long? defaultRecordPickerField2Id = null, long? defaultRecordPickerField3Id = null, bool? isShowInBar = null, CancellationToken ct = default);
    Task UpdateDefaultReportSettingsAsync(Guid publicId, string defaultReportSettings, CancellationToken ct = default);
    /// <summary>Sets (or clears, when null) the table's Custom Data Rule formula and its
    /// "Turn custom data rules on?" enabled flag together. Kept separate from
    /// <see cref="UpdateAsync"/> deliberately — unlike Name/Description/etc. there is no "leave
    /// unchanged" ambiguity to preserve here, and the Alias this rule can reference is intentionally
    /// not settable through any update path.</summary>
    Task UpdateCustomDataRuleAsync(Guid publicId, string? customDataRule, bool isEnabled, CancellationToken ct = default);
    /// <summary>Sets the table's key field (null = reset to Record ID#, the default).</summary>
    Task SetKeyFieldAsync(long tableId, long? keyFieldId, CancellationToken ct = default);
    /// <summary>Targeted update of just the three Identifying Records fields — used by table/field
    /// creation to auto-advance the picker slots without touching Name/Description/etc.</summary>
    Task SetDefaultRecordPickerFieldsAsync(long tableId, long? field1Id, long? field2Id, long? field3Id, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task IncrementRecordCountAsync(long id, CancellationToken ct = default);
    Task DecrementRecordCountAsync(long id, CancellationToken ct = default);
    Task DecrementRecordCountByAsync(long id, int count, CancellationToken ct = default);
}
