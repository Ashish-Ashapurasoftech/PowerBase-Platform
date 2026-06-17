namespace PowerBase.Application.Auth;

public class InviteTokenRecord
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long? TenantId { get; set; }
    public long? TenantRoleId { get; set; }
    public long? AppId { get; set; }
    public long? AppRoleId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public long InvitedBy { get; set; }
    public DateTime ExpiresOn { get; set; }
    public DateTime? UsedOn { get; set; }
    public DateTime CreatedOn { get; set; }
}
