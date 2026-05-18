namespace PowerBase.API.Models.Roles;

public class RoleResponse
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedOn { get; set; }
}
