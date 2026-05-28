namespace PowerBase.Application.Forms.Commands.ReorderFormRules;

public record ReorderFormRulesCommand(Guid FormPublicId, IReadOnlyList<Guid> OrderedRulePublicIds);
