namespace PowerBase.Application.Fields.Queries.ListFields;

public record ListFieldsQuery(
    Guid TablePublicId,
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string SortBy = "name",
    bool SortDesc = false,
    /// <summary>Either a dropdown value ("System Fields", "Required Fields", ...) or a field-type
    /// category (Text/Numeric/Date/Other/User/Formula/Relationship/Action). Null/"All Fields" = no filter.</summary>
    string? Filter = null);
