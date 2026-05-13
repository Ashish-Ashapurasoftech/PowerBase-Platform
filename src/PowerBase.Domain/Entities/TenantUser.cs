namespace PowerBase.Domain.Entities;

public class TenantUser
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long UserId { get; set; }
    public long? TenantRoleId { get; set; }
    public bool IsOwner { get; set; }
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime JoinedOn { get; set; }
    public long? InvitedBy { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
