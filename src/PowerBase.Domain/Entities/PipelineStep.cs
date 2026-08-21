namespace PowerBase.Domain.Entities;

public class PipelineStep
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long PipelineId { get; set; }
    public long? ParentStepId { get; set; }
    public string? ParentBranch { get; set; }
    public Guid? ParentPublicId { get; set; }
    public string RefId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsValidated { get; set; }
    public DateTime? LastTriggeredOn { get; set; }
    public int DisplayOrder { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Subtype { get; set; } = string.Empty;
    public string? ConfigJson { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
