namespace PowerBase.Application.Apps.Commands.DeleteAppVariable;

public record DeleteAppVariableCommand(
    Guid AppPublicId,
    Guid PublicId
);
