namespace PowerBase.API.Models.Apps;

public class CreateAppRoleRequest
{
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public string? ManageableRolesType { get; init; }
    public int? Rank { get; init; }
    public IReadOnlyList<Guid>? ManageableRolePublicIds { get; init; }
}
