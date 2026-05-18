namespace PowerBase.API.Models.Apps;

public class CreateAppRoleRequest
{
    public string Name { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}
