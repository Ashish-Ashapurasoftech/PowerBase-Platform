namespace PowerBase.Domain.Entities;

public class GroupApp
{
    public long Id { get; set; }
    public long GroupId { get; set; }
    public long AppId { get; set; }
    public long AppRoleId { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public bool IsDeleted { get; set; }
}
