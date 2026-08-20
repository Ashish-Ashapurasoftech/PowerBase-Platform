namespace PowerBase.API.Models.Apps;

public class AppRoleResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public bool IsSystem { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
    public string ManageableRolesType { get; init; } = "None";
    public int? Rank { get; init; }
    public IReadOnlyList<Guid> ManageableRolePublicIds { get; init; } = Array.Empty<Guid>();
}
