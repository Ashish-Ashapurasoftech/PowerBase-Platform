namespace PowerBase.Application.UserTokens.Commands.UpdateUserTokenStatus;

public class UpdateUserTokenStatusCommand
{
    public IEnumerable<Guid> PublicIds { get; set; } = Enumerable.Empty<Guid>();
    public bool IsActive { get; set; }
}
