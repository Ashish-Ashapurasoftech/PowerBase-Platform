using System;
using PowerBase.Application.Pipelines.Commands.CreatePipeline;

namespace PowerBase.Application.Pipelines.Commands.CopyPipeline;

public record CopyPipelineCommand(Guid SourcePipelinePublicId);
