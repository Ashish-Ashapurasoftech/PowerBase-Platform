using System;
using System.Text.Json.Serialization;

namespace PowerBase.Application.Common.Models;

public enum IndexAction
{
    Upsert,
    Delete,
    BackfillField,
    NullifyField,
    /// <summary>Re-reads every record of a table (page by page) and writes it to the index. Used to move a table to its own index and for recovery.</summary>
    ReindexTable
}

public class SearchIndexMessage
{
    public IndexAction Action { get; set; }
    public long TenantId { get; set; }
    public long AppId { get; set; }
    public long TableId { get; set; }
    public Guid RecordPublicId { get; set; }
    public long? FieldId { get; set; }
    public int Page { get; set; } = 1;
    /// <summary>Paging jobs (field backfill/clear, table reindex): continue with the records whose id is greater than this. Null on a
    /// message queued before this existed, which pages by <see cref="Page"/> once and continues by id from there.</summary>
    public long? AfterId { get; set; }
    /// <summary>Field jobs: the meta.SearchFieldJob row that tracks the job (null when the job is not recorded). Each page moves it forward;
    /// the last page removes it.</summary>
    public long? JobId { get; set; }
    /// <summary>For NullifyField of a deleted field: the index column(s) it used (EndSlot for a range's second value), which the table no longer tells.</summary>
    public string? Slot { get; set; }
    public string? EndSlot { get; set; }
    public long Version { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The indexed values the write set, for use inside this process only (for example to tell whether a write touched an indexed
    /// field). Never serialized: the queue message carries ids alone, so no field value (decrypted values of encrypted fields included)
    /// is stored in Service Bus. The indexer reads the record's current values from the database, which it can because every write
    /// sends its message only after it commits.
    /// </summary>
    [JsonIgnore]
    public System.Collections.Generic.Dictionary<string, object?>? Payload { get; set; }
}
