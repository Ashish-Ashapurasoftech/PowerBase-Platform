namespace PowerBase.Application.AppLogs;

/// <summary>
/// Collects what the current request (or background job) changed, for the application log. Scoped.
/// The audit calls handlers already make (IAuditRepository.LogActivity*) are forwarded here, so every change is described
/// without any handler knowing about this log. Record writes also attach the old / new values of every changed field
/// (the tenant's audit.ActivityLog keeps only the auditable ones, for the record page Stream).
/// Inside an HTTP request the AppLogMiddleware writes the collected events when the request ends (and drops them when it
/// failed, since its transaction rolled back); outside one (pipelines, workers) each event is queued as it happens.
/// </summary>
public interface IAppLogCollector
{
    void AddEvent(AppLogEvent appLogEvent);

    /// <summary>Old / new values (JSON of "Field label" → value) for the next event about <paramref name="entityId"/>.</summary>
    void AttachValues(string entityId, string? oldValues, string? newValues);
}

/// <summary>Hands finished log rows to the background writer (never blocks a request). Singleton.</summary>
public interface IAppLogQueue
{
    void Enqueue(AppLogEntry entry);
}

/// <summary>The logs database (applog.AppLog).</summary>
public interface IAppLogRepository
{
    Task InsertManyAsync(IReadOnlyList<AppLogEntry> entries, CancellationToken ct = default);
    Task<(IReadOnlyList<AppLogEntry> Items, int Total)> QueryAsync(AppLogFilter filter, CancellationToken ct = default);
    Task<AppLogEntry?> GetAsync(long tenantId, long id, CancellationToken ct = default);
}
