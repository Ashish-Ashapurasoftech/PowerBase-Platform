namespace PowerBase.Domain.Entities;

/// <summary>
/// A saved PowerFlows connection account ("Connect new account").
/// Lives in the tenant DB (meta.PipelineAccount) and is independent of any single
/// pipeline, unlike <see cref="PipelineConnection"/> which is a per-pipeline child row.
/// </summary>
public class PipelineAccount
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }

    /// <summary>Owning tenant — the tenant the account was created in.</summary>
    public long TenantId { get; set; }

    /// <summary>The logged-in user who created (and owns) this account.</summary>
    public long CreatedByUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Either <c>current_user</c> or <c>user_token</c>.</summary>
    public string AuthMode { get; set; } = string.Empty;

    /// <summary>Company subdomain / realm slug the account points at.</summary>
    public string Subdomain { get; set; } = string.Empty;

    /// <summary>Tenant the account grants access to (resolved from <see cref="Subdomain"/>).</summary>
    public long TargetTenantId { get; set; }

    /// <summary>User the supplied token belongs to — the identity work runs as.</summary>
    public long TargetUserId { get; set; }

    /// <summary>PublicId of the core.UserToken row, re-checked on every use.</summary>
    public Guid? UserTokenPublicId { get; set; }

    /// <summary>SHA-256 (hex, lowercase) of the supplied token. Never the raw token.</summary>
    public string? TokenHash { get; set; }

    /// <summary>Masked prefix for display only, e.g. <c>pb_ut_abc…</c>.</summary>
    public string? TokenPrefix { get; set; }

    /// <summary><c>active</c>, <c>revoked</c> or <c>unavailable</c>.</summary>
    public string Status { get; set; } = "active";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public bool IsDeleted { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>Allowed values for <see cref="PipelineAccount.AuthMode"/>.</summary>
public static class PipelineAccountAuthModes
{
    public const string CurrentUser = "current_user";
    public const string UserToken = "user_token";
}

/// <summary>Allowed values for <see cref="PipelineAccount.Status"/>.</summary>
public static class PipelineAccountStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string Unavailable = "unavailable";
}
