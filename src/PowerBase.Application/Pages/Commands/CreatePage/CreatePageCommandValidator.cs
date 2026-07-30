using FluentValidation;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;

namespace PowerBase.Application.Pages.Commands.CreatePage;

public class CreatePageCommandValidator : AbstractValidator<CreatePageCommand>
{
    public CreatePageCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.PageType)
            .Must(t => PageTypes.All.Contains(t))
            .WithMessage($"PageType must be one of: {string.Join(", ", PageTypes.All)}");
        RuleFor(x => x.Visibility)
            .NotEmpty()
            .Must(v => Enum.TryParse<Visibility>(v, out _))
            .WithMessage("Visibility must be one of: " + string.Join(", ", Enum.GetNames<Visibility>()));
        RuleFor(x => x.VisibleToRoleIds)
            .Must(ids => ids is { Count: > 0 })
            .When(x => x.Visibility == Visibility.SpecificRoles.ToString())
            .WithMessage("At least one role is required when Visibility is SpecificRoles.");
    }
}
