namespace PowerBase.Application.Apps.Commands.UpdateTablePermissions;

public record TablePermissionInput(
    Guid TablePublicId,
    string ViewScope,
    string ModifyScope,
    bool CanAdd,
    bool CanDelete,
    bool CanSaveSharedReports,
    bool CanEditFieldProperties,
    string FieldAccessLevel);

public record UpdateTablePermissionsCommand(
    Guid RolePublicId,
    IReadOnlyList<TablePermissionInput> Tables);
