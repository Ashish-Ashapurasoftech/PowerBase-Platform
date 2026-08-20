using FluentValidation;

namespace PowerBase.Application.Apps.Commands.CreateApp;

public class CreateAppCommandValidator : AbstractValidator<CreateAppCommand>
{
    public CreateAppCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.Icon).MaximumLength(100).When(x => x.Icon is not null);
        RuleFor(x => x.Color).MaximumLength(20).When(x => x.Color is not null);
        RuleForEach(x => x.Tables).ChildRules(tables =>
        {
            tables.RuleFor(t => t.Name).NotEmpty().MaximumLength(200);
            tables.RuleFor(t => t.SingularLabel).MaximumLength(200).When(t => t.SingularLabel is not null);
            tables.RuleFor(t => t.PluralLabel).MaximumLength(200).When(t => t.PluralLabel is not null);
            tables.RuleFor(t => t.Description).MaximumLength(500).When(t => t.Description is not null);
            tables.RuleFor(t => t.Icon).MaximumLength(100).When(t => t.Icon is not null);
            tables.RuleForEach(t => t.Fields).ChildRules(fields =>
            {
                fields.RuleFor(f => f.Label).NotEmpty().MaximumLength(200);
                fields.RuleFor(f => f.TypeCode).NotEmpty();
            });
        });
    }
}
