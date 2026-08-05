using FluentValidation;
using PowerBase.Domain.Enums;

namespace PowerBase.Application.Pages.Commands.UpdatePage;

public class UpdatePageCommandValidator : AbstractValidator<UpdatePageCommand>
{
    public UpdatePageCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.Visibility)
            .NotEmpty()
            .Must(v => Enum.TryParse<Visibility>(v, out _))
            .WithMessage("Visibility must be one of: " + string.Join(", ", Enum.GetNames<Visibility>()));
        RuleFor(x => x.VisibleToRoleIds)
            .Must(ids => ids is { Count: > 0 })
            .When(x => x.Visibility == Visibility.SpecificRoles.ToString())
            .WithMessage("At least one role is required when Visibility is SpecificRoles.");
        // Mandatory change note (spec requirement) — every edit must be attributable.
        RuleFor(x => x.ChangeNotes)
            .NotEmpty().WithMessage("A change note is required to save this page.")
            .MaximumLength(1000);
    }
}
