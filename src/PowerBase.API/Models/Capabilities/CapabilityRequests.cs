using System.ComponentModel.DataAnnotations;

namespace PowerBase.API.Models.Capabilities;

public class SaveRoleCapabilitiesRequest
{
    [Required]
    public Guid RoleId { get; set; }

    [Required]
    public List<string> Capabilities { get; set; } = new();
}

public class UpdateRoleCapabilityRequest
{
    [Required]
    public string Capability { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}
