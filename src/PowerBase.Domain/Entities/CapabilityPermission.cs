namespace PowerBase.Domain.Entities;

public class CapabilityPermission
{
    public long Id { get; set; }
    public long CapabilityId { get; set; }
    public long PermissionId { get; set; }
}
