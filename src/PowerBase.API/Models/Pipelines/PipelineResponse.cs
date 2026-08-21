using System;

namespace PowerBase.API.Models.Pipelines;

public class PipelineResponse
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedOn { get; set; }
}
