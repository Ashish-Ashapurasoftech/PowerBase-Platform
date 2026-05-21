namespace PowerBase.Application.Apps.Commands.UpdateAppVariable;

public record UpdateAppVariableCommand(
    Guid AppPublicId,
    Guid PublicId,
    string Name,
    string Value,
    string? Description
);
