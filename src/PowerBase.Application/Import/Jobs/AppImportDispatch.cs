using System.Text.Json;

namespace PowerBase.Application.Import.Jobs;

/// <summary>One queued background app import, as the worker that leased it sees it.</summary>
public sealed record AppImportQueueItem(long Id, long TenantId, Guid JobPublicId, string IdentityJson, Guid ClaimToken, int AttemptCount);

/// <summary>Who uploaded the file: the import runs with exactly these rights (stored with the queued job, no secrets).</summary>
public sealed record AppImportIdentity(long UserId, bool IsSuperAdmin, string UserName, string UserEmail, IReadOnlyList<string> Permissions, string TenantRole)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static AppImportIdentity FromJson(string json) => JsonSerializer.Deserialize<AppImportIdentity>(json, Json)!;
}

/// <summary>
/// Durable dispatch queue for background app imports (control database, meta.AppImportQueue): a job queued on any API instance is
/// leased and run by whichever instance is free; queued work survives restarts. Replaces the in-process channel (one instance only).
/// </summary>
public interface IAppImportDispatchQueue
{
    Task EnqueueAsync(long tenantId, Guid jobPublicId, AppImportIdentity identity, CancellationToken ct = default);

    /// <summary>Leases the oldest pending item, or one whose lease expired (its instance stopped). Null when there is none.</summary>
    Task<AppImportQueueItem?> ClaimAsync(string workerId, int leaseSeconds, CancellationToken ct = default);

    Task<bool> RenewLeaseAsync(long id, string workerId, Guid claimToken, int leaseSeconds, CancellationToken ct = default);

    Task CompleteAsync(long id, string workerId, Guid claimToken, CancellationToken ct = default);
}
