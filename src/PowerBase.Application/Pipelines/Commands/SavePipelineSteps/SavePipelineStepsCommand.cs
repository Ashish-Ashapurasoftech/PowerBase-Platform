using System;
using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Commands.SavePipelineSteps;

public class SavePipelineStepDto
{
    public Guid? PublicId { get; set; }
    public string RefId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsValidated { get; set; }
    public int DisplayOrder { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Subtype { get; set; } = string.Empty;
    public string? ConfigJson { get; set; }
    public List<SavePipelineStepDto>? Children { get; set; }
    public List<SavePipelineStepDto>? ElseChildren { get; set; }
    public List<SavePipelineStepDto>? SuccessChildren { get; set; }
    public List<SavePipelineStepDto>? ErrorChildren { get; set; }
}

public record SavePipelineStepsCommand(Guid PipelinePublicId, List<SavePipelineStepDto> Steps, byte[] RowVersion);

