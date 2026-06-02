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
        "eq", "ne", "contains", "notContains", "startsWith", "endsWith",
        "isEmpty", "isNotEmpty", "gt", "gte", "lt", "lte"
    };

    private static readonly HashSet<string> ValidConditionLogic = new() { "all", "any" };

    private static readonly HashSet<string> ValidTargetTypes = new() { "Field", "Section", "Block" };

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
            c.RuleFor(x => x.AppFieldId).GreaterThan(0);
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
