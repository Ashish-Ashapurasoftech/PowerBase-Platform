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

public record FormRuleConditionSpec(long AppFieldId, string Operator, string? Value, int DisplayOrder);

public record FormRuleActionSpec(
    string ActionType,
    string TargetType,
    long? TargetElementId,
    long? TargetSectionId,
    int DisplayOrder);
