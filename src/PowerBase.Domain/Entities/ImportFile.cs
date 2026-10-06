namespace PowerBase.Domain.Entities;

/// <summary>A CSV / Excel file a person uploaded to import from. Held only until it is imported, discarded, or a day has passed.
/// <see cref="StoragePath"/> is internal: clients refer to the file by <see cref="PublicId"/>.</summary>
public class ImportFile
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long UploadedByUserId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    /// <summary>"csv" or "xlsx".</summary>
    public string Format { get; set; } = "csv";
    public long SizeBytes { get; set; }
    public DateTime CreatedOn { get; set; }
}
