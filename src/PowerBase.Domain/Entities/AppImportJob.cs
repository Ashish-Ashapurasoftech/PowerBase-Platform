namespace PowerBase.Domain.Entities;

/// <summary>A QBL/PBL app import running (or finished) in the background.</summary>
public class AppImportJob
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long UserId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Status { get; set; } = AppImportJobStatus.Queued;
    public string? Stage { get; set; }
    public int PercentComplete { get; set; }
    public Guid? AppPublicId { get; set; }
    public string? AppName { get; set; }
    /// <summary>Applications the import tried to create (a QBL file can hold several).</summary>
    public int AppCount { get; set; } = 1;
    public int AppsCreated { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public byte[]? Content { get; set; }
    public string? MappingJson { get; set; }
    public string? FrontendBaseUrl { get; set; }
    public DateTime? EmailSentOn { get; set; }
    public string? EmailError { get; set; }
    public bool IsDismissed { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
}

public static class AppImportJobStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>A tenant's override of a built-in email template.</summary>
public class EmailTemplate
{
    public long Id { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
}
