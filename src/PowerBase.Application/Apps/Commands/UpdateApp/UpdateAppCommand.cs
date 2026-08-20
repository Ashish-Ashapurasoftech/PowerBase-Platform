using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Apps.Commands.UpdateApp;

public record UpdateAppCommand(Guid AppPublicId, string Name, string? Description, string? Icon, string? Color, AppFormattingSettings? Formatting, AppSecurityOptionsSettings? SecurityOptions, bool IsEncrypted = false);
