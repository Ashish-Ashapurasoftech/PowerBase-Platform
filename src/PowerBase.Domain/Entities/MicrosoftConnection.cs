namespace PowerBase.Domain.Entities;

/// <summary>
/// A Microsoft (Outlook) account connected by a PowerBase user (meta.MicrosoftConnection).
/// Shared across all pipelines of that user. Tokens are encrypted at rest.
/// </summary>
public class MicrosoftConnection
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long TenantId { get; set; }
    public long CreatedByUserId { get; set; }
    public string Provider { get; set; } = MicrosoftConnectionProviders.Outlook;
    public string MicrosoftUserId { get; set; } = string.Empty;
    public string AccountEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Scopes { get; set; } = string.Empty;
    public string EncryptedRefreshToken { get; set; } = string.Empty;
    public string? EncryptedAccessToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public string Status { get; set; } = MicrosoftConnectionStatuses.Connected;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public static class MicrosoftConnectionProviders
{
    public const string Outlook = "microsoft-outlook";
}

public static class MicrosoftConnectionStatuses
{
    public const string Connected = "connected";
    public const string ReconnectRequired = "reconnect_required";
}
