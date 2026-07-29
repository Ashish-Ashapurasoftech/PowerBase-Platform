namespace PowerBase.Application.UserTokens.Commands.RotateUserToken;

public class RotateUserTokenCommand
{
    public Guid PublicId { get; set; }

    public RotateUserTokenCommand(Guid publicId)
    {
        PublicId = publicId;
    }
}
