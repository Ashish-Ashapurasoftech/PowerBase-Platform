namespace PowerBase.Application.AppTokens.Commands.UpdateAppTokenStatus;

public class UpdateAppTokenStatusCommand
{
    public Guid AppPublicId { get; set; }
    public Guid PublicId { get; set; }
    public bool IsActive { get; set; }
}
