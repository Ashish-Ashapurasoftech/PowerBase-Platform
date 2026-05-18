namespace PowerBase.Application.Roles;

public record RoleResult(
    Guid PublicId,
    string Name,
    string? Description,
    bool IsDefault,
    bool IsSystem,
    DateTime CreatedOn);
