namespace PowerBase.Application.Apps.Commands.UpdateFieldPermissions;

public record FieldPermissionInput(Guid FieldPublicId, string Access);

public record UpdateFieldPermissionsCommand(
    Guid RolePublicId,
    Guid TablePublicId,
    IReadOnlyList<FieldPermissionInput> Fields);
