using FluentValidation;

namespace PowerBase.Application.Apps.Commands.UpdateAppRole;

public class UpdateAppRoleCommandValidator : AbstractValidator<UpdateAppRoleCommand>
{
    public UpdateAppRoleCommandValidator()
    {
        RuleFor(x => x.ManageableRolesType)
            .Must(x => x == null || x == "None" || x == "Below" || x == "Manual")
            .WithMessage("ManageableRolesType must be 'None', 'Below', or 'Manual'.");
        RuleFor(x => x.Rank)
            .Must(x => x == null || x > 0)
            .WithMessage("Rank must be a positive integer.");
    }
}
