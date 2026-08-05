namespace PowerBase.Application.UserTokens.Common;

public class UserTokenDto
{
    public Guid PublicId { get; set; }
    public string TokenName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TokenPrefix { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool AccessAllApps { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public IEnumerable<Guid> AllowedAppPublicIds { get; set; } = Enumerable.Empty<Guid>();
    public IEnumerable<string> AllowedAppNames { get; set; } = Enumerable.Empty<string>();
}

public class AdminUserTokenDto : UserTokenDto
{
    public long UserId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
}

public class UserTokenCreatedDto : UserTokenDto
{
    public string PlainTextToken { get; set; } = string.Empty;
}
