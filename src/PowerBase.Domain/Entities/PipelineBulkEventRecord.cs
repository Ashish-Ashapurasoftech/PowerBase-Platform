using System;

namespace PowerBase.Domain.Entities;

public class PipelineBulkEventRecord
{
    public long Id { get; set; }
    public Guid BulkEventId { get; set; }
    public int Ordinal { get; set; }
    public Guid RecordPublicId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? BeforeValuesJson { get; set; }
    public string? AfterValuesJson { get; set; }
    public string? ChangedFieldsJson { get; set; }
    public byte Processed { get; set; }
    public DateTime CreatedOn { get; set; }
}
