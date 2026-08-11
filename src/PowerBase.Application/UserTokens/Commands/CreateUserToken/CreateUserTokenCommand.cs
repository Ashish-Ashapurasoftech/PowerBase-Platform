namespace PowerBase.Application.UserTokens.Commands.CreateUserToken;

public class CreateUserTokenCommand
{
    public string TokenName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool AccessAllApps { get; set; }
    public IEnumerable<Guid>? AllowedAppPublicIds { get; set; }
}
