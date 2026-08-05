namespace PowerBase.Application.AppTokens.Commands.CreateAppToken;

public class CreateAppTokenCommand
{
    public Guid AppPublicId { get; set; }
    public string TokenName { get; set; } = string.Empty;
    public string? Description { get; set; }
}
