using System;

namespace PowerBase.Application.Pipelines.Queries.ListPipelines;

public record ListPipelinesQuery(
    Guid AppPublicId,
    int Page = 1,
    int PageSize = 10,
    string? Search = null,
    string SortBy = "createdOn",
    bool SortDesc = true,
    bool? IsActive = null);
