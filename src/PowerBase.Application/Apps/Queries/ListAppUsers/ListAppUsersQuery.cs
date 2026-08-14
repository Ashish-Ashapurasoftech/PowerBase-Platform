namespace PowerBase.Application.Apps.Queries.ListAppUsers;

public record ListAppUsersQuery(Guid AppPublicId,int Page,int PageSize,string? Search,string SortBy,bool SortDesc,string? Role,bool IsExport = false);
