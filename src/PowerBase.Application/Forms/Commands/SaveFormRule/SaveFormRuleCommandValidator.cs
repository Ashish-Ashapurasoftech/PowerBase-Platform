using FluentValidation;
using PowerBase.Domain.Enums;

namespace PowerBase.Application.Forms.Commands.SaveFormRule;

public class SaveFormRuleCommandValidator : AbstractValidator<SaveFormRuleCommand>
{
    private static readonly HashSet<string> ValidTriggers =
        Enum.GetNames<FormRunTrigger>().ToHashSet();

    private static readonly HashSet<string> ValidActionTypes =
        Enum.GetNames<FormRuleActionType>().ToHashSet();

    private static readonly HashSet<string> ValidOperators = new()
    {
        "eq", "ne", "contains", "notContains", "startsWith", "endsWith", "notStartsWith",
        "isEmpty", "isNotEmpty", "gt", "gte", "lt", "lte",
        // "includes"/"notIncludes" (User/MultiUser fields, and now role conditions) were already
        // used by the frontend's rule builder but missing here, which would have rejected any
        // rule using them — added alongside "changed"/"notChanged" (field-change-detection).
        "includes", "notIncludes", "changed", "notChanged",
        // Date-only relative-range containment ("is during the current/previous/next N <unit>") —
        // see FormRuleServerValidator.ComputeDuringRange for the evaluation side.
        "during", "notDuring",
    };

    private static readonly HashSet<string> ValidConditionLogic = new() { "all", "any" };

    private static readonly HashSet<string> ValidTargetTypes = new() { "Field", "Section", "Block" };

    private static readonly HashSet<string> ValidConditionKinds = new() { "field", "role" };

    public SaveFormRuleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.Tags).MaximumLength(500).When(x => x.Tags is not null);
        RuleFor(x => x.RowVersion).NotNull().NotEmpty().WithMessage("RowVersion is required.");
        RuleFor(x => x.RunTrigger)
            .Must(t => ValidTriggers.Contains(t))
            .WithMessage($"RunTrigger must be one of: {string.Join(", ", ValidTriggers)}");
        RuleFor(x => x.ConditionLogic)
            .Must(l => ValidConditionLogic.Contains(l))
            .WithMessage("ConditionLogic must be 'all' or 'any'.");

        RuleForEach(x => x.Conditions).ChildRules(c =>
        {
            c.RuleFor(x => x.ConditionKind)
                .Must(k => ValidConditionKinds.Contains(k))
                .WithMessage($"ConditionKind must be one of: {string.Join(", ", ValidConditionKinds)}");
            // 'field' conditions compare one field's value, so they need a real AppFieldId; 'role'
            // conditions compare the evaluating user's role instead, so AppFieldId is meaningless
            // there and must stay null (the field-picker vs. role-picker UI is mutually exclusive
            // per condition, mirroring the two condition kinds themselves).
            c.RuleFor(x => x.AppFieldId)
                .NotNull().GreaterThan(0)
                .When(x => x.ConditionKind == "field")
                .WithMessage("AppFieldId is required for a field condition.");
            c.RuleFor(x => x.AppFieldId)
                .Null()
                .When(x => x.ConditionKind == "role")
                .WithMessage("AppFieldId must not be set for a role condition.");
            c.RuleFor(x => x.Value)
                .NotEmpty()
                .When(x => x.ConditionKind == "role")
                .WithMessage("Value (the role(s) to match) is required for a role condition.");
            c.RuleFor(x => x.Operator)
                .Must(o => ValidOperators.Contains(o))
                .WithMessage($"Operator must be one of: {string.Join(", ", ValidOperators)}");
        });

        RuleForEach(x => x.Actions).ChildRules(a =>
        {
            a.RuleFor(x => x.ActionType)
                .Must(t => ValidActionTypes.Contains(t))
                .WithMessage($"ActionType must be one of: {string.Join(", ", ValidActionTypes)}");
            a.RuleFor(x => x.TargetType)
                .Must(t => ValidTargetTypes.Contains(t))
                .WithMessage("TargetType must be 'Field' or 'Section'.");
        });
    }
}
