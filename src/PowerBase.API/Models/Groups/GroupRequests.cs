namespace PowerBase.API.Models.Groups;

public class CreateGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class UpdateGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class AddGroupMembersRequest
{
    public IEnumerable<Guid> UserPublicIds { get; set; } = Enumerable.Empty<Guid>();
}

public class ShareGroupRequest
{
    public IEnumerable<Guid> AppPublicIds { get; set; } = Enumerable.Empty<Guid>();
    public Guid? AppRolePublicId { get; set; }
}
