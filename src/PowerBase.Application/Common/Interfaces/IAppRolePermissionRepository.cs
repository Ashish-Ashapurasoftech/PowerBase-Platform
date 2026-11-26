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

public record RecordFilterRow(Guid TablePublicId, string Conjunction, string FilterJson, string Kind = "View");

/// <summary>A role's builder permission <see cref="Code"/> does NOT apply to this table (Roles > Advanced Permissions).</summary>
public record RoleTableExclusionRow(long AppRoleId, string Code, long AppTableId, Guid TablePublicId);

/// <summary>A table of the app, slim.</summary>
public record AppTableRef(long Id, Guid PublicId, string Name);

/// <summary>A flat permission code held by a role.</summary>
public record RolePermissionCodeRow(long AppRoleId, string Code);

public interface IAppRolePermissionRepository
{
    // Table-scoped builder permissions (exclusions: a held code applies to every table except these)
    Task<IReadOnlyList<RoleTableExclusionRow>> GetTableExclusionsAsync(IReadOnlyCollection<long> appRoleIds, CancellationToken ct = default);
    /// <summary>Replaces the role's exclusions. Rows for codes the role does not hold are dropped.</summary>
    Task SetTableExclusionsAsync(long appRoleId, IReadOnlyList<(string Code, long AppTableId)> rows, CancellationToken ct = default);
    /// <summary>The flat permission codes of each of the given roles.</summary>
    Task<IReadOnlyList<RolePermissionCodeRow>> GetRoleCodesAsync(IReadOnlyCollection<long> appRoleIds, CancellationToken ct = default);
    /// <summary>The app's live tables, slim (id / public id / name only) — for the Table selection checks.</summary>
    Task<IReadOnlyList<AppTableRef>> ListAppTablesAsync(long appId, CancellationToken ct = default);
    /// <summary>True when one of the roles holds <paramref name="code"/> and does not exclude the table.</summary>
    Task<bool> HasTablePermissionAsync(IReadOnlyCollection<long> appRoleIds, string code, long appTableId, CancellationToken ct = default);

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
    /// <summary>Count of roles that have set this field's access to 'None' (used to enforce the required+default invariant).</summary>
    Task<int> CountRolesWithNoneAccessForFieldAsync(long appFieldId, CancellationToken ct = default);

    // Record-level row filters
    Task<IReadOnlyList<RecordFilterRow>> GetRecordFiltersAsync(long appRoleId, CancellationToken ct = default);
    Task SetRecordFiltersAsync(long appRoleId, IReadOnlyList<AppRoleRecordFilter> rows, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<AppRoleRecordFilter?> GetRecordFilterAsync(long appRoleId, long appTableId, CancellationToken ct = default);
    /// <summary>The role's Modify-scope Custom Rules row for the table (null when none).</summary>
    Task<AppRoleRecordFilter?> GetModifyRecordFilterAsync(long appRoleId, long appTableId, CancellationToken ct = default);

    // Seeding helpers
    /// <summary>Insert default permission rows for every table in the app for the given role (skips tables already configured).</summary>
    Task SeedDefaultsForRoleAsync(long appRoleId, long appId, CancellationToken ct = default);
    /// <summary>Insert default permission rows for every role in the app for the given table (skips roles already configured).</summary>
    Task SeedDefaultsForTableAsync(long appTableId, long appId, CancellationToken ct = default);
}
