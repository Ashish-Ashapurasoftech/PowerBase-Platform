using FluentValidation;

namespace PowerBase.Application.Apps.Commands.CreateAppRole;

public class CreateAppRoleCommandValidator : AbstractValidator<CreateAppRoleCommand>
{
    public CreateAppRoleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ManageableRolesType)
            .Must(x => x == null || x == "None" || x == "Below" || x == "Manual")
            .WithMessage("ManageableRolesType must be 'None', 'Below', or 'Manual'.");
        RuleFor(x => x.Rank)
            .NotNull().WithMessage("Rank is required.")
            .GreaterThan(0).WithMessage("Rank must be a positive integer.");
    }
}
