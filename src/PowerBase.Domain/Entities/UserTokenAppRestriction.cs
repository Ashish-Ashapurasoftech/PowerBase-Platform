namespace PowerBase.Domain.Entities;

public class UserTokenAppRestriction
{
    public long Id { get; set; }
    public long UserTokenId { get; set; }
    public long AppId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
