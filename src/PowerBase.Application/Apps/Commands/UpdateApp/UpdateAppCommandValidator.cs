using FluentValidation;

namespace PowerBase.Application.Apps.Commands.UpdateApp;

public class UpdateAppCommandValidator : AbstractValidator<UpdateAppCommand>
{
    public UpdateAppCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(200).WithMessage("Name cannot exceed 200 characters.");
    }
}
