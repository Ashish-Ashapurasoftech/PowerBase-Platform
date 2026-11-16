namespace PowerBase.API.Models.Apps;

public class CopyAppResponse
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEncrypted { get; set; }
    /// <summary>What the copy could not carry over as it is, for the result screen.</summary>
    public List<CopyAppWarningResponse> Warnings { get; set; } = new();
    /// <summary>Records copied (copy with data); null for a copy without data.</summary>
    public long? RecordsCopied { get; set; }
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
    /// <summary>What it is doing now, e.g. "Creating tables 7 of 13" or "Copying records: Employee 300,000 of 1,506,003".</summary>
    public string Stage { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public bool IsFailed { get; set; }
    /// <summary>Copy with data, once done: the new app. The dialog finishes from this when it lost the copy request.</summary>
    public Guid? AppPublicId { get; set; }
    /// <summary>Copy with data: records copied so far (all of them once done).</summary>
    public long? RecordsCopied { get; set; }
    /// <summary>Copy with data, once done: what could not be carried over.</summary>
    public List<CopyAppWarningResponse>? Warnings { get; set; }
    /// <summary>Copy with data that failed: why.</summary>
    public string? Error { get; set; }
}

/// <summary>One of the caller's copies with data, as the app list shows it.</summary>
public class AppCopyRunResponse
{
    /// <summary>The copy's app PublicId (opens the app once Done; dismisses the entry).</summary>
    public Guid Id { get; set; }
    /// <summary>Name of the copy.</summary>
    public string Name { get; set; } = string.Empty;
    public Guid SourceAppPublicId { get; set; }
    public string SourceAppName { get; set; } = string.Empty;
    /// <summary>Running, Done or Failed.</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>0–100.</summary>
    public int Percent { get; set; }
    /// <summary>What it is doing now, e.g. "Copying records: Employee 300,000 of 1,506,003".</summary>
    public string Stage { get; set; } = string.Empty;
    public long RecordsCopied { get; set; }
    public long TotalRecords { get; set; }
    public bool IncludeFiles { get; set; }
    /// <summary>Seconds since the copy started (until it ended, once finished).</summary>
    public long ElapsedSeconds { get; set; }
    /// <summary>Failed: why.</summary>
    public string? Error { get; set; }
}
