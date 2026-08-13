namespace PowerBase.Application.Groups.Commands.UpdateGroup;

public class UpdateGroupCommand
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}
