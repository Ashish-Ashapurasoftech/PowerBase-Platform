namespace PowerBase.Application.Fields.SnapshotJobs;

public static class FieldSnapshotJobStatus
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

/// <summary>One row of meta.FieldSnapshotJob (control DB): a Reinitialize of one Snapshot field across every record of its table.</summary>
public class FieldSnapshotJob
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long TenantId { get; set; }
    public Guid TablePublicId { get; set; }
    public string? TableName { get; set; }
    public Guid FieldPublicId { get; set; }
    public string? FieldLabel { get; set; }
    public long RequestedByUserId { get; set; }
    public bool IsSuperAdmin { get; set; }
    public string TenantRole { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int ProcessedCount { get; set; }
    public int ChangedCount { get; set; }
    /// <summary>Internal Id of the last record handled — the cursor a restarted worker resumes from.</summary>
    public long LastRecordId { get; set; }
    public string Status { get; set; } = FieldSnapshotJobStatus.Pending;
    public bool CancelRequested { get; set; }
    public Guid? ClaimToken { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
}

public sealed record FieldSnapshotJobProgress(bool OwnsLease, bool CancelRequested);

public interface IFieldSnapshotJobRepository
{
    Task<Guid> CreateAsync(FieldSnapshotJob job, CancellationToken ct = default);
    Task<FieldSnapshotJob?> GetByPublicIdAsync(Guid publicId, long tenantId, CancellationToken ct = default);
    /// <summary>The most recent job for the field (any status), or null when it never ran.</summary>
    Task<FieldSnapshotJob?> GetLatestForFieldAsync(long tenantId, Guid fieldPublicId, CancellationToken ct = default);
    /// <summary>True while a Pending or InProgress job exists for the field (prevents overlapping runs).</summary>
    Task<bool> HasActiveForFieldAsync(long tenantId, Guid fieldPublicId, CancellationToken ct = default);
    /// <summary>Leases Pending jobs and InProgress jobs whose lease expired (a crashed or restarted worker).</summary>
    Task<IReadOnlyList<FieldSnapshotJob>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, CancellationToken ct = default);
    /// <summary>Saves progress, renews the lease and reports whether a cancel was requested.</summary>
    Task<FieldSnapshotJobProgress> ReportProgressAsync(
        long id, string workerId, Guid claimToken, int processedCount, int changedCount, long lastRecordId, int leaseSeconds, CancellationToken ct = default);
    Task CompleteAsync(
        long id, string workerId, Guid claimToken, string status, int processedCount, int changedCount, string? error, CancellationToken ct = default);
    /// <summary>Cancels a Pending job at once; flags an InProgress one to stop after its current chunk.</summary>
    Task<bool> RequestCancelAsync(Guid publicId, long tenantId, CancellationToken ct = default);
}

/// <summary>Wakes the worker as soon as a job is queued instead of waiting for its next poll.</summary>
public static class FieldSnapshotJobWakeNotifier
{
    private static readonly SemaphoreSlim Signal = new(0, 1);

    public static void Wake()
    {
        if (Signal.CurrentCount == 0) try { Signal.Release(); } catch (SemaphoreFullException) { }
    }

    public static async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await Signal.WaitAsync(timeout, ct);
}
