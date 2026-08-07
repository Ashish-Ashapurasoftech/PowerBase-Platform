namespace PowerBase.Application.Groups.Commands.ShareGroupWithApp;

public class ShareGroupWithAppCommand
{
    public Guid GroupPublicId { get; set; }
    public IEnumerable<Guid> AppPublicIds { get; set; } = Enumerable.Empty<Guid>();
    public Guid? AppRolePublicId { get; set; }
}
