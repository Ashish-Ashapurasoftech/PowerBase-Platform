namespace PowerBase.Domain.Entities;

public class AppUser
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }
    public long UserId { get; set; }
    public Guid? UserPublicId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public long AppRoleId { get; set; }
    public string Status { get; set; } = "Active";
    public bool ShowInUserPickers { get; set; } = true;
    public long AddedBy { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? UpdatedOn { get; set; }
    public bool IsDeleted { get; set; }
}
