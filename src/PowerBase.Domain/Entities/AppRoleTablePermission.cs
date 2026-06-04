namespace PowerBase.Domain.Entities;

public class AppRoleTablePermission
{
    public long Id { get; set; }
    public long AppRoleId { get; set; }
    public long AppTableId { get; set; }
    public string ViewScope { get; set; } = "AllRecords";
    public string ModifyScope { get; set; } = "None";
    public bool CanAdd { get; set; }
    public bool CanDelete { get; set; }
    public bool CanSaveSharedReports { get; set; }
    public bool CanEditFieldProperties { get; set; }
    public string FieldAccessLevel { get; set; } = "FullAccess";
}
