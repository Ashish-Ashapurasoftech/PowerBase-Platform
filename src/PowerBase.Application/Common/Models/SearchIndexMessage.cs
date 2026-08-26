using System;

namespace PowerBase.Application.Common.Models;

public enum IndexAction
{
    Upsert,
    Delete,
    BackfillField,
    NullifyField
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
    public System.Collections.Generic.Dictionary<string, object?>? Payload { get; set; }
}
