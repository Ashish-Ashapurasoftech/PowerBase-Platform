namespace PowerBase.Application.Apps.Commands.UpdateApp;

public record UpdateAppCommand(
    Guid PublicId,
    string Name,
    string? Description,
    string? Icon,
    string? Color);
