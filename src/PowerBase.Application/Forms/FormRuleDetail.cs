namespace PowerBase.Application.Forms;

public class FormRuleDetail
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Tags { get; init; }
    public bool IsActive { get; init; }
    public bool IsExpressionMode { get; init; }
    public string? ExpressionText { get; init; }
    public string RunTrigger { get; init; } = "AnyChange";
    public string ConditionLogic { get; init; } = "all";
    public int DisplayOrder { get; init; }
    public DateTime CreatedOn { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public List<FormRuleConditionDetail> Conditions { get; init; } = [];
    public List<FormRuleActionDetail> Actions { get; init; } = [];
}

public class FormRuleConditionDetail
{
    public long Id { get; init; }
    public long AppFieldId { get; init; }
    public string Operator { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string? ValueType { get; init; }
    public long? ValueFieldId { get; init; }
    public int DisplayOrder { get; init; }
}

public class FormRuleActionDetail
{
    public long Id { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;
    public long? TargetElementId { get; init; }
    public long? TargetSectionId { get; init; }
    public long? TargetBlockId { get; init; }
    public string? ActionValue { get; init; }
    public int DisplayOrder { get; init; }
}
