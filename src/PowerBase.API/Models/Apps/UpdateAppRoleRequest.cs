namespace PowerBase.API.Models.Apps;

public record UpdateAppRoleRequest(IReadOnlyList<string> Permissions);
