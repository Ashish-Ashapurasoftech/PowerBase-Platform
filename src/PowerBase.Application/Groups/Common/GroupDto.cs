namespace PowerBase.Application.Groups.Common;

public class GroupDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedOn { get; set; }
}
