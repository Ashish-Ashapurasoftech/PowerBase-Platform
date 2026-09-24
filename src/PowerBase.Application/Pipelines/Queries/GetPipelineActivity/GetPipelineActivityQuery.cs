using System;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineActivity;

public record GetPipelineActivityQuery(
    Guid AppPublicId,
    Guid? PipelinePublicId,
    DateTime? FromDate,
    DateTime? ToDate,
    int Page = 1,
    int PageSize = 10);
