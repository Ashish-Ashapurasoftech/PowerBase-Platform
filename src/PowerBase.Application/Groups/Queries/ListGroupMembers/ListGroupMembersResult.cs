using PowerBase.Application.Groups.Common;

namespace PowerBase.Application.Groups.Queries.ListGroupMembers;

public class ListGroupMembersResult
{
    public IReadOnlyList<GroupMemberDto> Items { get; init; } = Array.Empty<GroupMemberDto>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}
