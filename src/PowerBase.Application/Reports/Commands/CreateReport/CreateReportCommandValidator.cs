using FluentValidation;
using PowerBase.Domain.Enums;

namespace PowerBase.Application.Reports.Commands.CreateReport;

public class CreateReportCommandValidator : AbstractValidator<CreateReportCommand>
{
    public CreateReportCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.Visibility)
            .NotEmpty()
            .Must(v => Enum.TryParse<Visibility>(v, out _))
            .WithMessage("Visibility must be one of: " + string.Join(", ", Enum.GetNames<Visibility>()));
    }
}
