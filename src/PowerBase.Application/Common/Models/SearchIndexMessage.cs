using System;

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
    /// <summary>For NullifyField of a deleted field: the index column(s) it used (EndSlot for a range's second value), which the table no longer tells.</summary>
    public string? Slot { get; set; }
    public string? EndSlot { get; set; }
    public long Version { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public System.Collections.Generic.Dictionary<string, object?>? Payload { get; set; }
}
