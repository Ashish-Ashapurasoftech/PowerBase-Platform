namespace PowerBase.API.Models.Apps;

public record UpdateAppRequest(string Name, string? Description, string? Icon, string? Color);
