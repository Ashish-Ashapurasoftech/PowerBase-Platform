namespace PowerBase.API.Models.Groups;

public class CreateGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? AppRolePublicId { get; set; }
}

public class UpdateGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? AppRolePublicId { get; set; }
}

public class AddGroupMembersRequest
{
    public IEnumerable<Guid> UserPublicIds { get; set; } = Enumerable.Empty<Guid>();
}

public class AssignGroupRoleRequest
{
    public Guid? AppRolePublicId { get; set; }
}

public class ShareGroupRequest
{
    public IEnumerable<Guid> AppPublicIds { get; set; } = Enumerable.Empty<Guid>();
}
