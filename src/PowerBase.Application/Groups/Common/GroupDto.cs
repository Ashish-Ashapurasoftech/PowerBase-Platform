namespace PowerBase.Application.Groups.Common;

public class GroupDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int MemberCount { get; set; }
    public DateTime CreatedOn { get; set; }
}

public class GroupMemberDto
{
    public Guid UserPublicId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public DateTime AddedOn { get; set; }
}

public class SharedAppDto
{
    public Guid AppPublicId { get; set; }
    public Guid? AppRolePublicId { get; set; }
    public string? AppRoleName { get; set; }
}

