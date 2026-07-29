namespace PowerBase.Application.UserTokens.Commands.RevokeUserToken;

public class RevokeUserTokenCommand
{
    public Guid PublicId { get; set; }

    public RevokeUserTokenCommand(Guid publicId)
    {
        PublicId = publicId;
    }
}
