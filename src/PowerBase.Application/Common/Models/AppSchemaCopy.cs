namespace PowerBase.Application.Common.Models;

/// <summary>Input of meta.usp_CopyAppSchema (Copy App – Schema Only). <paramref name="ProgressId"/>: when given, the
/// copy's progress is kept under this id for the dialog's progress bar.</summary>
public sealed record AppSchemaCopyRequest(
    long SourceAppId,
    string NewName,
    string? NewDescription,
    long UserId,
    Guid UserPublicId,
    string UserName,
    string UserEmail,
    bool CopyUsers,
    bool IsEncrypted,
    Guid? ProgressId = null);

/// <summary>How far a running copy is: 0–100 and the stage it is in. <paramref name="IsDone"/> once it ended, with
/// <paramref name="IsFailed"/> when it ended in an error.</summary>
public sealed record AppCopyProgress(int Percent, string Stage, bool IsDone, bool IsFailed);

/// <summary>Something the copy could not carry over as it is (connected table, code page, PowerFlows, app tokens…).</summary>
public sealed class AppCopyWarning
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Milliseconds one stage of the copy took, for the log.</summary>
public sealed class AppCopyStageTiming
{
    public string Stage { get; set; } = string.Empty;
    public int Ms { get; set; }
}

public sealed record AppSchemaCopyResult(
    long NewAppId,
    Guid NewAppPublicId,
    IReadOnlyList<AppCopyWarning> Warnings,
    IReadOnlyList<AppCopyStageTiming> Stages);
