namespace PowerBase.Domain.Entities;

public class PipelineConnection
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long PipelineId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string CredentialsJson { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
