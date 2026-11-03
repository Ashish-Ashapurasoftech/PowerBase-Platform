namespace PowerBase.API.Models.Apps;

public class CopyAppResponse
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEncrypted { get; set; }
    /// <summary>What the copy could not carry over as it is, for the result screen.</summary>
    public List<CopyAppWarningResponse> Warnings { get; set; } = new();
}

public class CopyAppWarningResponse
{
    /// <summary>CONNECTED_TABLE, CODE_PAGE, POWERFLOWS_NOT_COPIED or APP_TOKENS.</summary>
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>How far a running copy is, for the dialog's progress bar.</summary>
public class AppCopyProgressResponse
{
    /// <summary>0–100; 100 only once the copy is saved.</summary>
    public int Percent { get; set; }
    /// <summary>What it is doing now, e.g. "Creating tables 7 of 13".</summary>
    public string Stage { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public bool IsFailed { get; set; }
}
