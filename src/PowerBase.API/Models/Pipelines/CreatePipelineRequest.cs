namespace PowerBase.API.Models.Pipelines;

public class CreatePipelineRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}
