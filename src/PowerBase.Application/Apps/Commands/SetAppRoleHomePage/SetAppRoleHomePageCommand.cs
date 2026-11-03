namespace PowerBase.Application.Apps.Commands.SetAppRoleHomePage;

/// <summary>A null PagePublicId clears the role's home page (the role then falls back to the app default).</summary>
public record SetAppRoleHomePageCommand(Guid AppPublicId, Guid RolePublicId, Guid? PagePublicId);
