using System.Collections.Generic;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;

namespace PowerBase.API.Models.Pipelines;

public class SavePipelineStepsRequest
{
    public List<SavePipelineStepDto> Steps { get; set; } = new();
    public string RowVersion { get; set; } = string.Empty;
}
