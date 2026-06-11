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

    /// <summary>The current date in <see cref="TimeZone"/>, derived from <see cref="UtcNow"/>.</summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, TimeZone));

    public static EvaluationOptions Default => new();
}
