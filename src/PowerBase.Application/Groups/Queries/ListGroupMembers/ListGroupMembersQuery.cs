namespace PowerBase.Application.Groups.Queries.ListGroupMembers;

public class ListGroupMembersQuery
{
    public Guid GroupPublicId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
