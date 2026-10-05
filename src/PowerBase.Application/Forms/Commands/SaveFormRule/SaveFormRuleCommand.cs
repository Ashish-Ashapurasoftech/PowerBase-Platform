namespace PowerBase.Application.Forms.Commands.SaveFormRule;

public record SaveFormRuleCommand(
    Guid RulePublicId,
    string Name,
    string? Description,
    string? Tags,
    bool IsActive,
    string RunTrigger,
    string ConditionLogic,
    bool IsExpressionMode,
    string? ExpressionText,
    IReadOnlyList<FormRuleConditionSpec> Conditions,
    IReadOnlyList<FormRuleActionSpec> Actions,
    byte[] RowVersion);

public record FormRuleConditionSpec(
    long? AppFieldId, string Operator, string? Value, string? ValueType, long? ValueFieldId, int DisplayOrder,
    string ConditionKind = "field",
    string? ChangeFromOperator = null, string? ChangeFromValue = null, string? ChangeFromValueType = null, long? ChangeFromValueFieldId = null,
    string? ChangeToOperator = null, string? ChangeToValue = null, string? ChangeToValueType = null, long? ChangeToValueFieldId = null);

public record FormRuleActionSpec(
    string ActionType,
    string TargetType,
    long? TargetElementId,
    long? TargetSectionId,
    long? TargetBlockId,
    string? ActionValue,
    int DisplayOrder,
    bool RunOnceOnActivation = false,
    bool IsExpressionValue = false);
