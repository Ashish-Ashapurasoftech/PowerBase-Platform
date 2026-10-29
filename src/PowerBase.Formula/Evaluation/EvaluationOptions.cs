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

    /// <summary>Fills in the email/name of a user value that carries only an id (every user read from a
    /// record does). The host supplies it; left null, <c>UserToName</c>/<c>UserToEmail</c> of such a value are blank.
    /// Returns null when the id is unknown.</summary>
    public Func<string, UserRef?>? UserLookup { get; init; }

    /// <summary>The roles the current user has in the app the formula runs in, for <c>UserRoles()</c>. A function so the host only looks them up
    /// when a formula uses it. Left null, <c>UserRoles()</c> is blank.</summary>
    public Func<IReadOnlyList<RoleRef>>? CurrentUserRoles { get; init; }

    /// <summary>The roles of another user (by user id) in the same app, for <c>UserRoles(UserToId(...))</c>.</summary>
    public Func<string, IReadOnlyList<RoleRef>>? UserRolesOf { get; init; }

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>Identifier of the app the formula runs in, surfaced by <c>AppID()</c>. Empty when unknown.</summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>Identifier of the table the formula runs in, surfaced by <c>Dbid()</c>. Empty when unknown.</summary>
    public string TableId { get; init; } = string.Empty;

    /// <summary>Base URL of the app frontend (no trailing slash), surfaced by <c>URLRoot()</c>. Empty when unknown.</summary>
    public string UrlRoot { get; init; } = string.Empty;

    /// <summary>The page that triggered this evaluation, surfaced by <c>Rurl()</c> for "return here" links. Empty when unknown.</summary>
    public string ReturnUrl { get; init; } = string.Empty;

    /// <summary>The formula type of each readable field of the table the formula runs in, by Fid — what
    /// <c>GetFieldValue</c> needs to read a field whose ID is only known per record. Left null by hosts
    /// that don't supply it, in which case such a read yields blank.</summary>
    public IReadOnlyDictionary<long, FormulaType>? FieldTypes { get; init; }

    /// <summary>The current date in <see cref="TimeZone"/>, derived from <see cref="UtcNow"/>.</summary>
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, TimeZone));

    public static EvaluationOptions Default => new();
}
