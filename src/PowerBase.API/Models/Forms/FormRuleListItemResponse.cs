namespace PowerBase.API.Models.Forms;

public class FormRuleListItemResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Tags { get; init; }
    public bool IsActive { get; init; }
    public int ConditionCount { get; init; }
    public int ActionCount { get; init; }
    public int DisplayOrder { get; init; }
    public DateTime CreatedOn { get; init; }
}
