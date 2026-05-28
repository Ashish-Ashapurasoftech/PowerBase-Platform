namespace PowerBase.Domain.Entities;

public class AppRolePermission
{
    public long Id { get; set; }
    public long AppRoleId { get; set; }
    public int PermissionId { get; set; }
}
