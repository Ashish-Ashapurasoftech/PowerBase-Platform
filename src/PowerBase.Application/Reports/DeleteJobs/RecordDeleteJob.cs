using System.Text.Json;
using PowerBase.Application.Reports.Queries.RunReport;

namespace PowerBase.Application.Reports.DeleteJobs;

public static class RecordDeleteJobStatus
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

/// <summary>One row of meta.RecordDeleteJob (control DB).</summary>
public class RecordDeleteJob
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long TenantId { get; set; }
    public Guid ReportPublicId { get; set; }
    public Guid TablePublicId { get; set; }
    public string? TableName { get; set; }
    public long RequestedByUserId { get; set; }
    public bool IsSuperAdmin { get; set; }
    public string TenantRole { get; set; } = string.Empty;
    public string FilterJson { get; set; } = "{}";
    public int ExpectedCount { get; set; }
    public int DeletedCount { get; set; }
    public string Status { get; set; } = RecordDeleteJobStatus.Pending;
    public bool CancelRequested { get; set; }
    public Guid? ClaimToken { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
}

/// <summary>The report-run payload (everything the grid applied) frozen when the user confirmed, so the worker
/// resolves exactly the same set of records later, with no user session.</summary>
public class RecordDeleteJobFilter
{
    public List<RecordDeleteJobDynamicFilter>? DynamicFilters { get; set; }
    public string? QuickSearch { get; set; }
    public List<long>? SearchFieldIds { get; set; }
    public bool ExactMatch { get; set; }
    public FilterGroup? FilterTree { get; set; }
    public Dictionary<string, string>? AskAnswers { get; set; }

    public static RecordDeleteJobFilter FromQuery(RunReportQuery q) => new()
    {
        DynamicFilters = q.RuntimeFilters?.Select(f => new RecordDeleteJobDynamicFilter { FieldId = f.FieldId, Value = f.Value, SubField = f.SubField }).ToList(),
        QuickSearch = q.QuickSearch,
        SearchFieldIds = q.QuickSearchFieldIds?.ToList(),
        ExactMatch = q.QuickSearchExact,
        FilterTree = q.RuntimeFilterTree,
        AskAnswers = q.AskAnswers?.ToDictionary(k => k.Key, v => v.Value),
    };

    public string ToJson() => JsonSerializer.Serialize(this);

    public static RecordDeleteJobFilter FromJson(string json) =>
        JsonSerializer.Deserialize<RecordDeleteJobFilter>(json) ?? new RecordDeleteJobFilter();

    /// <summary>The report run for one chunk. Deleted records leave the result set, so page 1 is always the next chunk.</summary>
    public RunReportQuery ToQuery(Guid reportPublicId, int pageSize) => new(
        reportPublicId, 1, pageSize,
        DynamicFilters?.Select(f => (f.FieldId, f.Value, f.SubField)).ToList(),
        QuickSearch, SearchFieldIds, ExactMatch, FilterTree,
        null, false, null, false, true, AskAnswers);
}

public class RecordDeleteJobDynamicFilter
{
    public long FieldId { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? SubField { get; set; }
}

public sealed record RecordDeleteJobProgress(bool OwnsLease, bool CancelRequested);

public interface IRecordDeleteJobRepository
{
    Task<Guid> CreateAsync(RecordDeleteJob job, CancellationToken ct = default);
    Task<RecordDeleteJob?> GetByPublicIdAsync(Guid publicId, long tenantId, CancellationToken ct = default);
    /// <summary>True while a Pending or InProgress job exists for the table (prevents overlapping deletes).</summary>
    Task<bool> HasActiveForTableAsync(long tenantId, Guid tablePublicId, CancellationToken ct = default);
    /// <summary>Leases Pending jobs and InProgress jobs whose lease expired (a crashed or restarted worker).</summary>
    Task<IReadOnlyList<RecordDeleteJob>> ClaimAsync(string workerId, int batchSize, int leaseSeconds, CancellationToken ct = default);
    /// <summary>Saves progress, renews the lease and reports whether a cancel was requested.</summary>
    Task<RecordDeleteJobProgress> ReportProgressAsync(long id, string workerId, Guid claimToken, int deletedCount, int leaseSeconds, CancellationToken ct = default);
    Task CompleteAsync(long id, string workerId, Guid claimToken, string status, int deletedCount, string? error, CancellationToken ct = default);
    /// <summary>Cancels a Pending job at once; flags an InProgress one to stop after its current chunk.</summary>
    Task<bool> RequestCancelAsync(Guid publicId, long tenantId, CancellationToken ct = default);
}

/// <summary>Wakes the worker as soon as a job is queued instead of waiting for its next poll.</summary>
public static class RecordDeleteJobWakeNotifier
{
    private static readonly SemaphoreSlim Signal = new(0, 1);

    public static void Wake()
    {
        if (Signal.CurrentCount == 0) try { Signal.Release(); } catch (SemaphoreFullException) { }
    }

    public static async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await Signal.WaitAsync(timeout, ct);
}
