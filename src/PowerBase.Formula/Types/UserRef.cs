namespace PowerBase.Formula.Types;

/// <summary>
/// A user value as seen by a formula. Quickbase user fields carry both an id and
/// an email; <c>UserToID</c> / <c>UserToEmail</c> project out each component.
/// </summary>
public sealed record UserRef(string UserId, string? Email = null);
