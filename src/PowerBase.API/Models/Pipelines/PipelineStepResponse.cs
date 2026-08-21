using System;
using System.Collections.Generic;

namespace PowerBase.API.Models.Pipelines;

public class PipelineStepResponse
{
    public Guid PublicId { get; set; }
    public string RefId { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Subtype { get; set; } = string.Empty;
    public string? ConfigJson { get; set; }
    public string? ParentBranch { get; set; }
    public string RowVersion { get; set; } = string.Empty;

    public List<PipelineStepResponse> Children { get; set; } = new();
    public List<PipelineStepResponse> ElseChildren { get; set; } = new();
    public List<PipelineStepResponse> SuccessChildren { get; set; } = new();
    public List<PipelineStepResponse> ErrorChildren { get; set; } = new();
}
