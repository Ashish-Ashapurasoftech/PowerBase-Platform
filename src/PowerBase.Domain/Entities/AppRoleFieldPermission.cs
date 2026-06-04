namespace PowerBase.Domain.Entities;

public class AppRoleFieldPermission
{
    public long Id { get; set; }
    public long AppRoleId { get; set; }
    public long AppFieldId { get; set; }
    public string Access { get; set; } = "Modify";
}
