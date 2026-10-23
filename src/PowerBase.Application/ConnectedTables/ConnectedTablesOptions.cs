namespace PowerBase.Application.ConnectedTables;

/// <summary>Settings of the Connected Tables module, from the "ConnectedTables" configuration section. Every value has a
/// default, so the section is optional.</summary>
public sealed class ConnectedTablesOptions
{
    public const string SectionName = "ConnectedTables";

    /// <summary>At most this many refreshes read from one source realm at the same time.</summary>
    public int MaxConcurrentPerProvider { get; set; } = 2;
    /// <summary>Refreshes one API instance runs at the same time.</summary>
    public int MaxConcurrentRuns { get; set; } = 4;
    /// <summary>A connected table may not copy more source records than this.</summary>
    public int MaxSourceRows { get; set; } = 2_000_000;
    /// <summary>A connected table may not have more connected fields than this.</summary>
    public int MaxConnectedFields { get; set; } = 200;
    /// <summary>Source records read per chunk.</summary>
    public int ChunkSize { get; set; } = 2000;
    /// <summary>Pause between chunks, to go easy on a busy source database.</summary>
    public int ChunkDelayMilliseconds { get; set; }
    /// <summary>Transient failures (timeouts, a source database briefly unreachable) are retried this many times.</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>After this many failed scheduled refreshes in a row, the schedule is switched to Manual only.</summary>
    public int FailureThreshold { get; set; } = 5;
    /// <summary>When true, a refresh reads only source records changed since the last successful refresh (when it can).</summary>
    public bool IncrementalRefresh { get; set; } = true;
    /// <summary>Every Nth refresh is a full one even when incremental refresh is possible (drift check).</summary>
    public int FullRefreshEvery { get; set; } = 24;
    /// <summary>Overlap applied to the incremental watermark, covering clock differences between servers.</summary>
    public int WatermarkOverlapMinutes { get; set; } = 5;

    public int EffectiveChunkSize => ChunkSize is >= 100 and <= 5000 ? ChunkSize : 2000;
}
