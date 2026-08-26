namespace PowerBase.Application.Capabilities.Dtos;

public class CapabilityDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();
}
