namespace PowerBase.Formula.Types;

/// <summary>
/// A user value as seen by a formula. Quickbase user fields carry an id, an email and a display
/// name; <c>UserToID</c> / <c>UserToEmail</c> / <c>UserToName</c> project out each component.
///
/// Only the id is always present. A record's stored user value is just an identifier, so
/// <see cref="Email"/> and <see cref="Name"/> are filled in only where the host actually knows
/// them — today that means <c>User()</c>, the current user.
/// </summary>
public sealed record UserRef(string UserId, string? Email = null, string? Name = null);
