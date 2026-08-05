namespace PowerBase.Domain.Entities;

public class User
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string EmailNormalized { get; set; } = string.Empty;
    public string HashedPassword { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEmailVerified { get; set; }
    public int? SystemRoleId { get; set; }
    public string? SystemRoleCode { get; set; }
    public bool IsActive { get; set; }
    public string? Preferences { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? LastLoginOn { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
