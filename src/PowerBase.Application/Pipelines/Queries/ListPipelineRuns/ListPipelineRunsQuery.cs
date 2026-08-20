using System;
using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Queries.ListPipelineRuns;

public record ListPipelineRunsQuery(
    Guid PipelinePublicId,
    int Page = 1,
    int PageSize = 10
);

public record PipelineRunDto(
    Guid PublicId,
    string Status,
    string TriggerType,
    DateTime StartedOn,
    DateTime? CompletedOn,
    string TriggeredByUser,
    string? ErrorMessage,
    int AttemptCount
);

public record PipelineRunsResult(
    IReadOnlyList<PipelineRunDto> Items,
    int TotalCount,
    int Page,
    int PageSize
);
