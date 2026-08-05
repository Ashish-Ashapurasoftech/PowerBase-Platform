namespace PowerBase.Application.Groups.Commands.AssignGroupRole;

public class AssignGroupRoleCommand
{
    public Guid GroupPublicId { get; set; }
    public Guid? AppRolePublicId { get; set; }
}
