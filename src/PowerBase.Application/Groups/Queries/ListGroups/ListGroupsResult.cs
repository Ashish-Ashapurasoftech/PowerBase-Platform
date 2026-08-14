using PowerBase.Application.Groups.Common;

namespace PowerBase.Application.Groups.Queries.ListGroups;

public class ListGroupsResult
{
    public IReadOnlyList<GroupDto> Items { get; init; } = Array.Empty<GroupDto>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}
