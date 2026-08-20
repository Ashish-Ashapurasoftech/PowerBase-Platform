namespace PowerBase.Application.Apps.Queries.ListApps;

public record ListAppsQuery(int Page, int PageSize, string? SortField = null, bool SortDescending = false);
