namespace PowerBase.Application.Fields.Queries.ListFields;

/// <summary>Every field on a table in one call — search/sort/filter still apply, but there is no
/// page/pageSize at all. Backs the Fields settings grid and Field Detail's Prev/Next, both of
/// which need the complete, unpaginated field list rather than a slice of it.</summary>
public record ListAllFieldsQuery(
    Guid TablePublicId,
    string? Search = null,
    string SortBy = "name",
    bool SortDesc = false,
    string? Filter = null);
