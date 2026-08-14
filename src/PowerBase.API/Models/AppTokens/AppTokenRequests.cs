namespace PowerBase.API.Models.AppTokens;

public record CreateAppTokenRequest(string TokenName, string? Description);

public record UpdateAppTokenStatusRequest(bool IsActive);
