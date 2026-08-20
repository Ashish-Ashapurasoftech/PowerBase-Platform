using System;
using System.Collections.Generic;

namespace PowerBase.Application.Pipelines.Queries.GetPipeline;

public class PipelineResult
{
    public Guid PublicId { get; set; }
    public Guid AppPublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? VariablesJson { get; set; }
    public bool IsActive { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public List<PipelineStepResult> Steps { get; set; } = new();
}
