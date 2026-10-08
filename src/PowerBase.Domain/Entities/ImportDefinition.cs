namespace PowerBase.Domain.Entities;

/// <summary>A saved, re-runnable import into <see cref="DestinationTableId"/>. The JSON columns hold the
/// versioned mapping / conditions / rules documents (see PowerBase.Application.Imports).</summary>
public class ImportDefinition
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }
    public long DestinationTableId { get; set; }
    public string SourceKind { get; set; } = "table";
    public long? SourceTableId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ImportType { get; set; } = "copy";
    public int? MergeKeyFid { get; set; }
    public string? ConditionsJson { get; set; }
    public string FieldMappingJson { get; set; } = "[]";
    public string? ColumnRulesJson { get; set; }
    public string? OptionsJson { get; set; }
    public string? ScheduleJson { get; set; }
    public DateTime? NextRunOn { get; set; }
    public long RunAsUserId { get; set; }
    public bool NeedsAttention { get; set; }
    public string? AttentionReason { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
}
