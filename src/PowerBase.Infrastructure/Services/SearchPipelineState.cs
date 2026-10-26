using System.Collections.Concurrent;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// What the search pipeline is doing right now (last time each worker looked at its queue, what the outbox holds, how much was indexed
/// or rejected), kept in memory for the health endpoint. One per process; it holds counts and times only, never record data.
/// </summary>
public sealed class SearchPipelineState
{
    private readonly ConcurrentDictionary<string, DateTime> _workerLoopUtc = new();
    private readonly ConcurrentDictionary<string, DateTime> _workerLastBatchUtc = new();
    private long _indexed;
    private long _rejected;
    private long _deadLettered;
    private long _outboxPending;
    private long _outboxParked;
    private long _outboxOldestTicks;
    private long _relayCycleTicks;
    private long _statsTicks;

    /// <summary>A worker went round its receive loop (even if the queue was empty): it is alive.</summary>
    public void MarkWorkerLoop(string worker) => _workerLoopUtc[worker] = DateTime.UtcNow;

    public void MarkBatch(string worker, int indexedDocuments, int rejectedDocuments)
    {
        _workerLastBatchUtc[worker] = DateTime.UtcNow;
        Interlocked.Add(ref _indexed, indexedDocuments);
        Interlocked.Add(ref _rejected, rejectedDocuments);
    }

    public void MarkDeadLettered() => Interlocked.Increment(ref _deadLettered);

    public void MarkRelayCycle() => Interlocked.Exchange(ref _relayCycleTicks, DateTime.UtcNow.Ticks);

    public void SetOutbox(long pending, long parked, DateTime? oldestPendingUtc)
    {
        Interlocked.Exchange(ref _outboxPending, pending);
        Interlocked.Exchange(ref _outboxParked, parked);
        Interlocked.Exchange(ref _outboxOldestTicks, oldestPendingUtc?.Ticks ?? 0);
        Interlocked.Exchange(ref _statsTicks, DateTime.UtcNow.Ticks);
    }

    public IReadOnlyDictionary<string, DateTime> WorkerLoops => _workerLoopUtc;
    public IReadOnlyDictionary<string, DateTime> WorkerLastBatches => _workerLastBatchUtc;
    public long Indexed => Interlocked.Read(ref _indexed);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long DeadLettered => Interlocked.Read(ref _deadLettered);
    public long OutboxPending => Interlocked.Read(ref _outboxPending);
    public long OutboxParked => Interlocked.Read(ref _outboxParked);
    public DateTime? OutboxOldestPendingUtc => Interlocked.Read(ref _outboxOldestTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;
    public DateTime? RelayLastCycleUtc => Interlocked.Read(ref _relayCycleTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;
    public DateTime? OutboxStatsUtc => Interlocked.Read(ref _statsTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;
}
