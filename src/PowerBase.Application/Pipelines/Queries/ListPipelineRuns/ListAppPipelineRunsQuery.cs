using System;
using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Queries.ListPipelineRuns;

/// <summary>Cross-pipeline Activity view for an app: every run of every pipeline in that app,
/// optionally narrowed to one pipeline and/or a date range.</summary>
public record ListAppPipelineRunsQuery(
    Guid AppPublicId,
    Guid? PipelinePublicId = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int Page = 1,
    int PageSize = 10
);

public record AppPipelineRunDto(
    Guid PublicId,
    Guid PipelinePublicId,
    string PipelineName,
    string Status,
    string TriggerType,
    DateTime StartedOn,
    DateTime? CompletedOn,
    string TriggeredByUser,
    string? ErrorMessage,
    int AttemptCount
);

public record AppPipelineRunsResult(
    IReadOnlyList<AppPipelineRunDto> Items,
    int TotalCount,
    int Page,
    int PageSize
);
