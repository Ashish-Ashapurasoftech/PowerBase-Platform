namespace PowerBase.Application.Groups.Commands.AddGroupMembers;

public class AddGroupMembersCommand
{
    public Guid GroupPublicId { get; set; }
    public IEnumerable<Guid> UserPublicIds { get; set; } = Enumerable.Empty<Guid>();
}
