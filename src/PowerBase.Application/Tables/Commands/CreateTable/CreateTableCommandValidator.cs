using FluentValidation;

namespace PowerBase.Application.Tables.Commands.CreateTable;

public class CreateTableCommandValidator : AbstractValidator<CreateTableCommand>
{
    public CreateTableCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SingularLabel).MaximumLength(200).When(x => x.SingularLabel is not null);
        RuleFor(x => x.PluralLabel).MaximumLength(200).When(x => x.PluralLabel is not null);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.Icon).MaximumLength(100).When(x => x.Icon is not null);

        // Same per-table field rules as CreateAppCommandValidator.
        RuleFor(x => x.Fields)
            .Must(fields => fields!
                .Where(f => !string.IsNullOrWhiteSpace(f.Label))
                .GroupBy(f => f.Label.Trim(), StringComparer.OrdinalIgnoreCase)
                .All(g => g.Count() == 1))
            .WithMessage("Field name already exists in this table. Duplicate field names are not allowed.")
            .When(x => x.Fields is { Count: > 1 });

        RuleForEach(x => x.Fields).ChildRules(fields =>
        {
            fields.RuleFor(f => f.Label).NotEmpty().MaximumLength(200);
            fields.RuleFor(f => f.TypeCode).NotEmpty();
        });
    }
}
