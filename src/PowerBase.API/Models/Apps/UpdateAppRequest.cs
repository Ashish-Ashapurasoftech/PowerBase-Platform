using PowerBase.Domain.ValueObjects;

namespace PowerBase.API.Models.Apps;

public record UpdateAppRequest(string Name, string? Description, string? Icon, string? Color, AppFormattingSettings? Formatting, AppSecurityOptionsSettings? SecurityOptions);
