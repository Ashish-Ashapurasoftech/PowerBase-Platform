using System;
using System.Collections.Generic;

namespace PowerBase.API.Models.Pipelines;

public class PipelineDetailResponse
{
    public Guid PublicId { get; set; }
    public Guid AppPublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? VariablesJson { get; set; }
    public bool IsActive { get; set; }
    public string RowVersion { get; set; } = string.Empty;
    public List<PipelineStepResponse> Steps { get; set; } = new();
}
