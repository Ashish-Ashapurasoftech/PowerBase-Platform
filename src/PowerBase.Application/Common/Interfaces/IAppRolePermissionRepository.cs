using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

// ── Read projections (carry PublicId/name for the API layer) ──────────────────

public record TablePermissionRow(
    Guid TablePublicId, string TableName,
    string ViewScope, string ModifyScope,
    bool CanAdd, bool CanDelete, bool CanSaveSharedReports, bool CanEditFieldProperties,
    string FieldAccessLevel);

public record FieldPermissionRow(Guid FieldPublicId, string FieldName, string Access);

public record FieldPermissionScopedRow(Guid TablePublicId, Guid FieldPublicId, string Access);

public record RecordFilterRow(Guid TablePublicId, string Conjunction, string FilterJson);

public interface IAppRolePermissionRepository
{
    // Table-level
    Task<IReadOnlyList<TablePermissionRow>> GetTablePermissionsAsync(long appRoleId, CancellationToken ct = default);
    Task SetTablePermissionsAsync(long appRoleId, IReadOnlyList<AppRoleTablePermission> rows, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<AppRoleTablePermission?> GetTablePermissionAsync(long appRoleId, long appTableId, CancellationToken ct = default);

    // Field-level (per table)
    Task<IReadOnlyList<FieldPermissionRow>> GetFieldPermissionsAsync(long appRoleId, long appTableId, CancellationToken ct = default);
    Task SetFieldPermissionsAsync(long appRoleId, long appTableId, IReadOnlyList<AppRoleFieldPermission> rows, IDbTransaction? transaction = null, CancellationToken ct = default);
    /// <summary>AppFieldId → Access ('View' | 'Modify' | 'None') for fields with a non-default entry.</summary>
    Task<IReadOnlyDictionary<long, string>> GetFieldAccessMapAsync(long appRoleId, long appTableId, CancellationToken ct = default);
    /// <summary>All stored field permissions for a role across every table (for the runtime read path).</summary>
    Task<IReadOnlyList<FieldPermissionScopedRow>> GetAllFieldPermissionsAsync(long appRoleId, CancellationToken ct = default);

    // Record-level row filters
    Task<IReadOnlyList<RecordFilterRow>> GetRecordFiltersAsync(long appRoleId, CancellationToken ct = default);
    Task SetRecordFiltersAsync(long appRoleId, IReadOnlyList<AppRoleRecordFilter> rows, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<AppRoleRecordFilter?> GetRecordFilterAsync(long appRoleId, long appTableId, CancellationToken ct = default);
}
