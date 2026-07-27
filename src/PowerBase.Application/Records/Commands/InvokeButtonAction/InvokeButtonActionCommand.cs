namespace PowerBase.Application.Records.Commands.InvokeButtonAction;

/// <summary>A click on an Action Button field. Exactly one of <see cref="PromptValue"/> /
/// <see cref="CapturedFileRef"/> is populated depending on the button's variant; the rest
/// are ignored. <see cref="ClientNow"/> is informational only — the server clock is the
/// sole authority for Link Expiration (spec: enforced "at the moment of click", not just
/// used to control client-side visibility).</summary>
public sealed record InvokeButtonActionCommand(
    Guid TablePublicId,
    Guid RecordPublicId,
    int ButtonFid,
    string? PromptValue,
    string? CapturedFileRef,
    string? Password,
    double? GeoLat,
    double? GeoLng,
    string? GeoState,
    DateTime? ClientNow);

public sealed class InvokeButtonActionResult
{
    /// <summary>Fid → value, for every field the click actually wrote. The caller applies
    /// these in place (Rule 2: no page refresh).</summary>
    public IReadOnlyDictionary<long, object?> UpdatedFields { get; init; } = new Dictionary<long, object?>();

    /// <summary>Resolved redirect URL, or null when the button has no Redirect configured
    /// (in which case the client should just apply UpdatedFields in place).</summary>
    public string? Redirect { get; init; }
}
