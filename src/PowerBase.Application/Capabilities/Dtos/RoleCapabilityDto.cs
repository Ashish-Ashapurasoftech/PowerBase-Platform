namespace PowerBase.Application.Capabilities.Dtos;

public class RoleCapabilityDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = "none";
    public IReadOnlyList<string> Permissions { get; set; } = Array.Empty<string>();
}
