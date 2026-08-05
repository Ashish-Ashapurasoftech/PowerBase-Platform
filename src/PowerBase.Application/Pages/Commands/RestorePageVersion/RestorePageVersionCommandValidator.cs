using FluentValidation;

namespace PowerBase.Application.Pages.Commands.RestorePageVersion;

public class RestorePageVersionCommandValidator : AbstractValidator<RestorePageVersionCommand>
{
    public RestorePageVersionCommandValidator()
    {
        RuleFor(x => x.VersionNo).GreaterThan(0);
        RuleFor(x => x.ChangeNotes).NotEmpty().WithMessage("A change note is required to restore a version.").MaximumLength(1000);
    }
}
