namespace PowerBase.Application.Common.Interfaces;

public interface IAppAccessService
{
    Task RequirePermissionByAppPublicIdAsync(Guid appPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByTablePublicIdAsync(Guid tablePublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByReportPublicIdAsync(Guid reportPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByAppIdAsync(long appId, string permissionCode, CancellationToken ct = default);
    Task RequireAppRoleAsync(Guid appPublicId, string roleName, CancellationToken ct = default);
    Task RequirePermissionByFormPublicIdAsync(Guid formPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByFormRulePublicIdAsync(Guid rulePublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByPagePublicIdAsync(Guid pagePublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByPipelinePublicIdAsync(Guid pipelinePublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByRolePublicIdAsync(Guid rolePublicId, string permissionCode, CancellationToken ct = default);
    /// <summary>Checks permission via the app that owns the specified relationship.</summary>
    Task RequirePermissionByRelationshipPublicIdAsync(Guid relationshipPublicId, string permissionCode, CancellationToken ct = default);

    /// <summary>Ensures the user is a member of the app that owns the specified table. Does NOT require any specific permission code.</summary>
    Task RequireMembershipByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
    /// <summary>
    /// The user may add records to the table: an app member one of whose roles has "Add records" on it (Data Permissions,
    /// the table-level CanAdd — the same rule as adding a record by hand). Super admins always may. Used by imports in place
    /// of the flat <c>records:create</c> permission, which roles can no longer be given from the role page.
    /// </summary>
    Task RequireCanAddRecordsByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
    /// <summary>Ensures the user is a member of the app that owns the specified report. Does NOT require any specific permission code.</summary>
    Task RequireMembershipByReportPublicIdAsync(Guid reportPublicId, CancellationToken ct = default);
    /// <summary>Ensures the user is a member of the app that owns the specified page. Does NOT require any specific permission code.
    /// Used for the render endpoint, which is data-access governed by the page's own role visibility, not a flat permission code.</summary>
    Task RequireMembershipByPagePublicIdAsync(Guid pagePublicId, CancellationToken ct = default);
    /// <summary>Ensures the user is a member of the given app directly (the route already gives
    /// the app's own public id — no owning-entity lookup needed, unlike the ByTable/ByReport/
    /// ByPage variants above). Does NOT require any specific permission code — used by
    /// viewer-facing endpoints scoped to the whole app rather than one entity in it (e.g. the
    /// nav-pages list, which every app member should see regardless of their Pages permission).</summary>
    Task RequireMembershipByAppPublicIdAsync(Guid appPublicId, CancellationToken ct = default);
}
