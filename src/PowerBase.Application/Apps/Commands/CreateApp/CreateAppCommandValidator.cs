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
        RuleFor(x => x.TableName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TableSingularLabel).MaximumLength(200).When(x => x.TableSingularLabel is not null);
        RuleFor(x => x.TablePluralLabel).MaximumLength(200).When(x => x.TablePluralLabel is not null);
        RuleFor(x => x.TableDescription).MaximumLength(500).When(x => x.TableDescription is not null);
        RuleFor(x => x.TableIcon).MaximumLength(100).When(x => x.TableIcon is not null);
    }
}
