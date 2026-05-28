namespace PowerBase.API.Models.Forms;

public class FormRuleDetailResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Tags { get; init; }
    public bool IsActive { get; init; }
    public string RunTrigger { get; init; } = string.Empty;
    public string ConditionLogic { get; init; } = string.Empty;
    public bool IsExpressionMode { get; init; }
    public string? ExpressionText { get; init; }
    public int DisplayOrder { get; init; }
    public string RowVersion { get; init; } = string.Empty;
    public List<FormRuleConditionResponse> Conditions { get; init; } = [];
    public List<FormRuleActionResponse> Actions { get; init; } = [];
    public DateTime CreatedOn { get; init; }
}

public class FormRuleConditionResponse
{
    public long AppFieldId { get; init; }
    public string Operator { get; init; } = string.Empty;
    public string? Value { get; init; }
    public int DisplayOrder { get; init; }
}

public class FormRuleActionResponse
{
    public string ActionType { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;
    public long? TargetElementId { get; init; }
    public long? TargetSectionId { get; init; }
    public int DisplayOrder { get; init; }
}
