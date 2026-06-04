using PowerBase.Domain.Constants;

namespace PowerBase.Domain.Entities;

public class AppRoleTablePermission
{
    public long Id { get; set; }
    public long AppRoleId { get; set; }
    public long AppTableId { get; set; }
    public string ViewScope { get; set; } = RecordScopes.AllRecords;
    public string ModifyScope { get; set; } = RecordScopes.None;
    public bool CanAdd { get; set; }
    public bool CanDelete { get; set; }
    public bool CanSaveSharedReports { get; set; }
    public bool CanEditFieldProperties { get; set; }
    public string FieldAccessLevel { get; set; } = TableFieldAccessLevels.FullAccess;

    /// <summary>
    /// Returns the canonical default permission row for a role×table pair.
    /// ViewScope=AllRecords so the role can see data; all write/structural permissions off.
    /// This is the safe starting point before an admin explicitly restricts access.
    /// </summary>
    public static AppRoleTablePermission Default(long appRoleId, long appTableId) => new()
    {
        AppRoleId = appRoleId,
        AppTableId = appTableId,
        ViewScope = RecordScopes.AllRecords,
        ModifyScope = RecordScopes.None,
        CanAdd = false,
        CanDelete = false,
        CanSaveSharedReports = false,
        CanEditFieldProperties = false,
        FieldAccessLevel = TableFieldAccessLevels.FullAccess,
    };
}
