namespace PowerBase.Application.Apps.Commands.UpdateAppRole;

public record UpdateAppRoleCommand(
    Guid AppPublicId,
    Guid RolePublicId,
    bool CanViewRecords,
    bool CanAddRecords,
    bool CanEditRecords,
    bool CanDeleteRecords);
