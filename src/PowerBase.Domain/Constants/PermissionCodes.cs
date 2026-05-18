namespace PowerBase.Domain.Constants;

public static class PermissionCodes
{
    public const string AppsCreate   = "apps:create";
    public const string AppsRead     = "apps:read";
    public const string AppsUpdate   = "apps:update";
    public const string AppsDelete   = "apps:delete";

    public const string TablesCreate = "tables:create";
    public const string TablesRead   = "tables:read";
    public const string TablesDelete = "tables:delete";

    public const string FieldsCreate = "fields:create";
    public const string FieldsRead   = "fields:read";

    public const string RecordsCreate = "records:create";
    public const string RecordsRead   = "records:read";
    public const string RecordsUpdate = "records:update";
    public const string RecordsDelete = "records:delete";

    public const string ReportsCreate = "reports:create";
    public const string ReportsRead   = "reports:read";
    public const string ReportsRun    = "reports:run";

    public const string UsersInvite  = "users:invite";
    public const string UsersManage  = "users:manage";
    public const string RolesManage  = "roles:manage";

    public static readonly IReadOnlySet<string> DefaultUserPermissions = new HashSet<string>
    {
        AppsRead,
        TablesRead,
        FieldsRead,
        RecordsCreate, RecordsRead, RecordsUpdate, RecordsDelete,
        ReportsRead, ReportsRun,
    };
}
