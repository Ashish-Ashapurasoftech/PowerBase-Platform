using System;

namespace PowerBase.Application.Pipelines.Commands.CreatePipeline;

public record CreatePipelineCommand(Guid AppPublicId, string Name, string? Description);

public record CreatePipelineResult(Guid PublicId, string Name, string? Description, bool IsActive, DateTime CreatedOn);
