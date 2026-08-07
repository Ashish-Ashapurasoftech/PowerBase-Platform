namespace PowerBase.API.Models.Apps;

public class AppUserResponse
{
    public Guid PublicId { get; init; }
    public Guid UserPublicId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public string UserEmail { get; init; } = string.Empty;
    public Guid RolePublicId { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool ShowInUserPickers { get; init; } = true;
    public string AddedOn { get; init; } = string.Empty;
    public bool IsOwner { get; init; }
    public bool IsFromGroup { get; init; }
}
