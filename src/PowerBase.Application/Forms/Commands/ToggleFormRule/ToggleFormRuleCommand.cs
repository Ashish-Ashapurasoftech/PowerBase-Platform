namespace PowerBase.Application.Forms.Commands.ToggleFormRule;

public record ToggleFormRuleCommand(Guid RulePublicId, bool IsActive);
