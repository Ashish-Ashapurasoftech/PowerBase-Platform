namespace PowerBase.Application.Forms.Commands.DuplicateFormRule;

public record DuplicateFormRuleCommand(Guid RulePublicId, string? Name = null);
