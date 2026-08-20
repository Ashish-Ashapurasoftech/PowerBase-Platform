using System;
using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Queries.GetPipeline;

public class PipelineStepResult
{
    public Guid PublicId { get; set; }
    public string RefId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsValidated { get; set; }
    public DateTime? LastTriggeredOn { get; set; }
    public int DisplayOrder { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Subtype { get; set; } = string.Empty;
    public string? ConfigJson { get; set; }
    public string? ParentBranch { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<PipelineStepResult> Children { get; set; } = new();
    public List<PipelineStepResult> ElseChildren { get; set; } = new();
    public List<PipelineStepResult> SuccessChildren { get; set; } = new();
    public List<PipelineStepResult> ErrorChildren { get; set; } = new();
}
