namespace PowerBase.Application.Apps.Commands.CreateApp;

public record CreateAppCommand(
    string Name,
    string? Description,
    string? Icon,
    string? Color
);
