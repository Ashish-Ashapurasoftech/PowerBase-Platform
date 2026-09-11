namespace PowerBase.Application.Apps.Queries.ListAppUsers;

public record ListAppUsersQuery(
    Guid AppPublicId,
    int Page,
    int PageSize,
    string? Search,
    string SortBy,
    bool SortDesc,
    string? Role = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? AccessTypes = null,
    IReadOnlyList<string>? UserPickerFilters = null,
    IReadOnlyList<string>? Groups = null,
    bool IsExport = false);
