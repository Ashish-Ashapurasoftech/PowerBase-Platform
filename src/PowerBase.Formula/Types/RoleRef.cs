namespace PowerBase.Formula.Types;

/// <summary>A role as seen by <c>UserRoles()</c>: the identifier the host shows for it, and its name.</summary>
public sealed record RoleRef(string Id, string Name);
