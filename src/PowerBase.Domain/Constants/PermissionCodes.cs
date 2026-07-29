namespace PowerBase.Domain.Constants;

public static class PermissionCodes
{
    public const string AppsCreate   = "apps:create";
    public const string AppsRead     = "apps:read";
    public const string AppsUpdate   = "apps:update";
    public const string AppsDelete   = "apps:delete";

    public const string TablesCreate = "tables:create";
    public const string TablesRead   = "tables:read";
    public const string TablesUpdate = "tables:update";
    public const string TablesDelete = "tables:delete";

    public const string FieldsCreate = "fields:create";
    public const string FieldsRead   = "fields:read";
    public const string FieldsUpdate = "fields:update";
    public const string FieldsDelete = "fields:delete";

    public const string RecordsCreate = "records:create";
    public const string RecordsRead   = "records:read";
    public const string RecordsUpdate = "records:update";
    public const string RecordsDelete = "records:delete";

    public const string ReportsCreate = "reports:create";
    public const string ReportsRead   = "reports:read";
    public const string ReportsUpdate = "reports:update";
    public const string ReportsDelete = "reports:delete";
    public const string ReportsRun    = "reports:run";

    public const string UsersInvite  = "users:invite";
    public const string UsersManage  = "users:manage";
    public const string RolesManage  = "roles:manage";

    public const string AuditLogsRead = "audit:read";
    public const string AuditLogsReadOfStream = "records:stream";

    public const string FormsCreate      = "forms:create";
    public const string FormsRead        = "forms:read";
    public const string FormsUpdate      = "forms:update";
    public const string FormsDelete      = "forms:delete";
    public const string FormsRulesManage = "forms:rules:manage";

    public const string TokenCreate      = "token:create";

    /// <summary>
    /// Structural/admin permission codes assigned to regular app members by default.
    /// Record data access (view/add/modify/delete/field-level) is governed by table-level
    /// permissions configured per role in AppRoleTablePermission, not these flat codes.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultUserPermissions = new HashSet<string>
    {
        AppsRead,
        TablesRead,
        FieldsRead,
        ReportsRead, ReportsRun,
        FormsRead,
        TokenCreate,
    };
}
