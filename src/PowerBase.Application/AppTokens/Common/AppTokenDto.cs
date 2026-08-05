namespace PowerBase.Application.AppTokens.Common;

public class AppTokenDto
{
    public Guid PublicId { get; set; }
    public Guid AppPublicId { get; set; }
    public long CreatedByUserId { get; set; }
    public string TokenName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TokenPrefix { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class AppTokenCreatedDto : AppTokenDto
{
    public string PlainTextToken { get; set; } = string.Empty;
}
