namespace PowerBase.Application.Groups.Queries.ListGroups;

public class ListGroupsQuery
{
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string SortBy { get; set; } = "name";
    public bool SortDesc { get; set; } = false;
}
