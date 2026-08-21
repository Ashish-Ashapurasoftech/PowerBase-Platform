using System;

namespace PowerBase.Application.Pipelines.Queries.ListPipelines;

public record PipelineListItem(Guid PublicId, string Name, string? Description, bool IsActive, DateTime CreatedOn, string? FirstStepType, string? FirstStepSubtype);
