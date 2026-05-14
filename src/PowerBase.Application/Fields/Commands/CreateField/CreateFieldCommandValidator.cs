using FluentValidation;
using PowerBase.Domain.Enums;

namespace PowerBase.Application.Fields.Commands.CreateField;

public class CreateFieldCommandValidator : AbstractValidator<CreateFieldCommand>
{
    private static readonly string[] ValidTypeCodes = Enum.GetNames<FieldTypeCode>();

    public CreateFieldCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TypeCode)
            .NotEmpty()
            .Must(c => ValidTypeCodes.Contains(c))
            .WithMessage($"TypeCode must be one of: {string.Join(", ", ValidTypeCodes)}");
        RuleFor(x => x.Label).MaximumLength(200).When(x => x.Label is not null);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
    }
}
