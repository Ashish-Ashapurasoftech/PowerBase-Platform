namespace PowerBase.Application.Groups.Commands.RemoveGroupMember;

public class RemoveGroupMemberCommand
{
    public Guid GroupPublicId { get; set; }
    public Guid UserPublicId { get; set; }
}
