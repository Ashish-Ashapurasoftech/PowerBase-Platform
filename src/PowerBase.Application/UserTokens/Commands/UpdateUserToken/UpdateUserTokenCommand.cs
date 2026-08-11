using FluentValidation;

namespace PowerBase.Application.UserTokens.Commands.UpdateUserToken;

public class UpdateUserTokenCommand
{
    public Guid PublicId { get; set; }
    public string TokenName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool AccessAllApps { get; set; }
    public IEnumerable<Guid>? AllowedAppPublicIds { get; set; }
}
