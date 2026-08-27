namespace PowerBase.Application.Apps.Queries.ListApps;

public record ListAppsQuery(int Page, int PageSize, string? Search = null, string? SortField = null, bool SortDescending = false);
