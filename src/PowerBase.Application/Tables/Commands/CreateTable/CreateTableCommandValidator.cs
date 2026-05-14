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
    }
}
