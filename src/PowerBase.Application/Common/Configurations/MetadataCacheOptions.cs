namespace PowerBase.Application.Common.Configurations;

/// <summary>Settings of the metadata cache, from the "MetadataCache" section of the configuration. Every value has a default, so the section is optional.</summary>
public class MetadataCacheOptions
{
    public const string SectionName = "MetadataCache";

    /// <summary>Kill switch: false makes every read go to the database, exactly as before the cache existed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Backstop only — entries are normally dropped by the write that changes them.</summary>
    public int TtlHours { get; set; } = 24;

    /// <summary>How long a group stays uncached after a write, so a read that was already running cannot store the old data.</summary>
    public int WriteQuarantineSeconds { get; set; } = 5;

    /// <summary>Same, for a write inside a database transaction, which becomes visible only when the caller commits.</summary>
    public int TransactionQuarantineSeconds { get; set; } = 30;
}
