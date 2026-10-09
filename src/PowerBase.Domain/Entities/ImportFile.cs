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
    /// <summary>Set when a run has used the file: it is kept until then (so the run's history can show it) instead of being deleted when the
    /// run ends. Null for a file nobody has imported, which is not kept past a day.</summary>
    public DateTime? RetainedUntil { get; set; }
}
