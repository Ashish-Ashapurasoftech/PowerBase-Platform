namespace PowerBase.Application.Groups.Commands.CreateGroup;

public class CreateGroupCommand
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? AppRolePublicId { get; set; }
}
