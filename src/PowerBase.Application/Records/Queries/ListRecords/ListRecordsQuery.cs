namespace PowerBase.Application.Records.Queries.ListRecords;

/// <param name="FilterFid">When set together with FilterValue, adds a single 'eq' filter on that field Fid (used by Report Link "related" views).</param>
/// <param name="FilterValue">The value to match for the FilterFid filter.</param>
public record ListRecordsQuery(
    Guid TablePublicId,
    int Page,
    int PageSize,
    int? FilterFid = null,
    string? FilterValue = null);
