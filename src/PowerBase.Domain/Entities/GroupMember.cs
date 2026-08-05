namespace PowerBase.Domain.Entities;

public class GroupMember
{
    public long Id { get; set; }
    public long GroupId { get; set; }
    public long UserId { get; set; }
    public long AddedBy { get; set; }
    public DateTime CreatedOn { get; set; }
    public bool IsDeleted { get; set; }
}
