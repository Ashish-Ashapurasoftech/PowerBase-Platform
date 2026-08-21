using System;
using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Commands.DeletePipelines;

public record DeletePipelinesCommand(Guid AppPublicId, IReadOnlyList<Guid> PipelinePublicIds);
