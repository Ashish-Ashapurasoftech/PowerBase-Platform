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

/// <summary>Input of a copy with data (Copy App – Schema + Data): the structure as in <see cref="AppSchemaCopyRequest"/>,
/// then the records. <paramref name="IncludeFiles"/>: File values come along, each file and revision copied for the new app,
/// or are left empty.</summary>
public sealed record AppDataCopyRequest(AppSchemaCopyRequest Schema, bool IncludeFiles);

/// <summary>A finished copy with data: the new app, what could not be carried over, the live records copied, and the copied
/// tables that have records (to be indexed for search).</summary>
public sealed record AppDataCopyResult(
    long NewAppId,
    Guid NewAppPublicId,
    IReadOnlyList<AppCopyWarning> Warnings,
    long RecordsCopied,
    IReadOnlyList<long> TablesWithRecords);

/// <summary>How far a running copy is: 0–100 and the stage it is in. <paramref name="IsDone"/> once it ended, with
/// <paramref name="IsFailed"/> when it ended in an error. A copy with data also tells, once it is done, the new app,
/// the records copied and the warnings, or why it failed: the dialog may have lost the copy request itself (an HTTP
/// response is cut after the hosting timeout) and then finishes from this.</summary>
public sealed record AppCopyProgress(
    int Percent,
    string Stage,
    bool IsDone,
    bool IsFailed,
    Guid? NewAppPublicId = null,
    long? RecordsCopied = null,
    IReadOnlyList<AppCopyWarning>? Warnings = null,
    string? Error = null);

/// <summary>A copy with data as the app list shows it (meta.AppCopyRun). <paramref name="Id"/> is the copy's app PublicId.
/// <paramref name="Status"/>: Running, Done or Failed. <paramref name="ElapsedSeconds"/> is worked out by the database, so
/// no clock or time zone of the browser is involved.</summary>
public sealed record AppCopyRunSummary(
    Guid Id,
    string Name,
    Guid SourceAppPublicId,
    string SourceAppName,
    string Status,
    int Percent,
    string Stage,
    long RecordsCopied,
    long TotalRecords,
    bool IncludeFiles,
    long ElapsedSeconds,
    string? Error);

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
