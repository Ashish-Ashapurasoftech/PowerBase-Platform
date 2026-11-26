namespace PowerBase.Application.Common.Configurations;

/// <summary>
/// Protection of tenant databases against the API's heaviest reads ("SqlWorkload" section; doc 21): whole-table streaming scans
/// (reports, lists, exports and View rules over every row), id scans and the in-memory read. Interactive point reads are not touched.
/// </summary>
public sealed class SqlWorkloadOptions
{
    public const string SectionName = "SqlWorkload";

    /// <summary>Kill switch: false = no limiting (today's behaviour before this setting existed).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Heavy read batches running at once per tenant database, per API instance. More wait their turn.</summary>
    public int HeavyReadsPerTenantDb { get; set; } = 4;

    /// <summary>Azure SQL load (max of CPU, data IO, log write from sys.dm_db_resource_stats) at which each heavy batch waits
    /// <see cref="SlowDelayMs"/> first; at <see cref="StopPercent"/> it waits <see cref="StopDelayMs"/>. Never fails a request.</summary>
    public int SlowPercent { get; set; } = 80;
    public int StopPercent { get; set; } = 95;
    public int SlowDelayMs { get; set; } = 100;
    public int StopDelayMs { get; set; } = 1000;

    /// <summary>How long one load reading is reused (the DMV has one row per 15 s).</summary>
    public int LoadCacheSeconds { get; set; } = 5;
}
