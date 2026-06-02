namespace PowerBase.API.Models.Forms;

public class DefaultFormSettingsResponse
{
    public Guid EveryoneEditFormId { get; init; }
    public Guid EveryoneAddFormId { get; init; }
    public List<RoleFormOverrideResponse> Roles { get; init; } = [];
    public List<FormListItemResponse> Forms { get; init; } = [];
}

public class RoleFormOverrideResponse
{
    public Guid RoleId { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public Guid? EditFormId { get; init; }
    public Guid? AddFormId { get; init; }
}



public class UpdateDefaultFormSettingsRequest
{
    public Guid EveryoneEditFormId { get; init; }
    public Guid EveryoneAddFormId { get; init; }
    public List<RoleFormOverrideRequestItem> RoleOverrides { get; init; } = [];
}

public class RoleFormOverrideRequestItem
{
    public Guid RoleId { get; init; }
    public Guid? EditFormId { get; init; }
    public Guid? AddFormId { get; init; }
}
