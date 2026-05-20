namespace PowerBase.Application.Apps.Commands.UpdateApp;

public record UpdateAppCommand(Guid AppPublicId, string Name, string? Description, string? Icon, string? Color);
