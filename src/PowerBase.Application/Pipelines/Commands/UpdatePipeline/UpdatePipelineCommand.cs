using System;

namespace PowerBase.Application.Pipelines.Commands.UpdatePipeline;

public record UpdatePipelineCommand(Guid PublicId, string Name, string? Description, bool IsActive, byte[] RowVersion);
