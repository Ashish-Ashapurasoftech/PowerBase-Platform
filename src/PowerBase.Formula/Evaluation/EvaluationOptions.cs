using PowerBase.Formula.Types;

namespace PowerBase.Formula.Evaluation;

/// <summary>
/// Ambient inputs an evaluation needs beyond the record itself: the clock (so
/// <c>Now()</c>/<c>Today()</c> are deterministic and testable), the current user
/// for <c>User()</c>, and the time zone used to derive a local "today".
/// </summary>
public sealed class EvaluationOptions
{
    public DateTime UtcNow { get; init; } = DateTime.UtcNow;

    public UserRef? CurrentUser { get; init; }

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>Identifier of the app the formula runs in, surfaced by <c>AppID()</c>. Empty when unknown.</summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>Identifier of the table the formula runs in, surfaced by <c>Dbid()</c>. Empty when unknown.</summary>
    public string TableId { get; init; } = string.Empty;

    /// <summary>Base URL of the app frontend (no trailing slash), surfaced by <c>URLRoot()</c>. Empty when unknown.</summary>
    public string UrlRoot { get; init; } = string.Empty;

    /// <summary>The current date in <see cref="TimeZone"/>, derived from <see cref="UtcNow"/>.</summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, TimeZone));

    public static EvaluationOptions Default => new();
}
