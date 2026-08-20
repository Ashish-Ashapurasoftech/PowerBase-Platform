namespace PowerBase.Application.Tables.Queries.ListTables;

public record ListTablesQuery(
    Guid AppPublicId,
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string SortBy = "name",
    bool SortDesc = false,
    /// <summary>When supplied, only tables matching this flag are returned (sidebar use case).
    /// When null, the full table listing is returned.</summary>
    bool? IsShowInBar = null);
