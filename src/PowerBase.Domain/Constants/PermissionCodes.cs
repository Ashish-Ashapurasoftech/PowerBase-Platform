namespace PowerBase.Domain.Constants;

public static class PermissionCodes
{
    public const string AppsCreate   = "apps:create";
    public const string AppsRead     = "apps:read";
    public const string AppsUpdate   = "apps:update";
    public const string AppsDelete   = "apps:delete";
    public const string AppsCopy     = "apps:copy";

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

    public const string PagesCreate  = "pages:create";
    public const string PagesRead    = "pages:read";
    public const string PagesUpdate  = "pages:update";
    public const string PagesDelete  = "pages:delete";
    public const string PagesPublish = "pages:publish";
    /// <summary>Code Page Builder capability — additionally required (on top of PagesCreate/
    /// PagesUpdate) to create or edit a page whose PageType is "Code".</summary>
    public const string PagesCode    = "pages:code";
    public const string PowerFlowsCreate = "PowerFlows:create";
    public const string PowerFlowsRead   = "PowerFlows:read";
    public const string PowerFlowsUpdate = "PowerFlows:update";
    public const string PowerFlowsDelete = "PowerFlows:delete";
    public const string PowerFlowsCopy   = "PowerFlows:copy";

    public const string TokenCreate      = "token:create";

    // Branding (App Settings > Branding & Layout): which parts of the app's branding a role may change.
    public const string BrandingColor  = "branding:color";
    public const string BrandingFont   = "branding:font";
    public const string BrandingGrid   = "branding:grid";
    public const string BrandingLayout = "branding:layout";

    public static readonly IReadOnlySet<string> BrandingPermissions = new HashSet<string>
    {
        BrandingColor, BrandingFont, BrandingGrid, BrandingLayout,
    };

    /// <summary>
    /// Builder permissions a role can narrow to some tables (Roles > Advanced Permissions > Table selection;
    /// meta.AppRolePermissionTableExclusion). A role holding one of these has it on every table unless excluded.
    /// </summary>
    public static readonly IReadOnlySet<string> TableScoped = new HashSet<string>
    {
        TablesRead, TablesUpdate, TablesDelete,
        FieldsCreate, FieldsRead, FieldsUpdate, FieldsDelete,
        ReportsCreate, ReportsRead, ReportsUpdate, ReportsDelete,
        FormsCreate, FormsRead, FormsUpdate, FormsDelete, FormsRulesManage,
    };

    /// <summary>
    /// The table-scoped permissions the API enforces per table. The *:read ones are left out on purpose: the same codes
    /// load a table's fields, reports and forms for everyday use, so a per-table "View" only gates the builder screens
    /// (enforced in the client) and never blocks working with the table's records.
    /// </summary>
    public static readonly IReadOnlySet<string> TableScopedEnforced = new HashSet<string>
    {
        TablesUpdate, TablesDelete,
        FieldsCreate, FieldsUpdate, FieldsDelete,
        ReportsCreate, ReportsUpdate, ReportsDelete,
        FormsCreate, FormsUpdate, FormsDelete, FormsRulesManage,
    };

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
        PagesRead,
        PowerFlowsRead,
        TokenCreate,
    };
}
