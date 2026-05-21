namespace PowerBase.Application.Apps.Commands.CreateAppVariable;

public record CreateAppVariableCommand(
    Guid AppPublicId,
    string Name,
    string Value,
    string? Description
);
