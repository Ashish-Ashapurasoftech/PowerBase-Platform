namespace PowerBase.Application.Groups.Commands.UnshareGroupFromApp;

public class UnshareGroupFromAppCommand
{
    public Guid AppPublicId { get; set; }
    public Guid GroupPublicId { get; set; }
}
