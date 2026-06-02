namespace PowerBase.API.Models.Forms;

public class SaveFormRuleRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Tags { get; init; }
    public bool IsActive { get; init; } = true;
    public string RunTrigger { get; init; } = "AnyChange";
    public string ConditionLogic { get; init; } = "all";
    public bool IsExpressionMode { get; init; }
    public string? ExpressionText { get; init; }
    public List<FormRuleConditionRequest> Conditions { get; init; } = [];
    public List<FormRuleActionRequest> Actions { get; init; } = [];
    public string RowVersion { get; init; } = string.Empty;
}

public class FormRuleConditionRequest
{
    public long AppFieldId { get; init; }
    public string Operator { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string? ValueType { get; init; }
    public long? ValueFieldId { get; init; }
    public int DisplayOrder { get; init; }
}


public class FormRuleActionRequest
{
    public string ActionType { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;
    public long? TargetElementId { get; init; }
    public long? TargetSectionId { get; init; }
    public long? TargetBlockId { get; init; }
    public string? ActionValue { get; init; }
    public int DisplayOrder { get; init; }
}
