namespace PowerBase.API.Models.Apps;

public class AppRoleResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public bool IsSystem { get; init; }
}
