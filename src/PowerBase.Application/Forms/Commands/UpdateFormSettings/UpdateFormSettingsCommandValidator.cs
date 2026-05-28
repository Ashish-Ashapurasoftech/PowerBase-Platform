using FluentValidation;

namespace PowerBase.Application.Forms.Commands.UpdateFormSettings;

public class UpdateFormSettingsCommandValidator : AbstractValidator<UpdateFormSettingsCommand>
{
    private static readonly HashSet<string> ValidSaveOptions = new()
    {
        "SaveKeepWorking", "SaveNew", "SaveNext", "SaveView"
    };

    public UpdateFormSettingsCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.RowVersion).NotNull().NotEmpty().WithMessage("RowVersion is required for optimistic concurrency.");
        RuleFor(x => x.SaveOptions)
            .Must(BeValidSaveOptions)
            .WithMessage($"SaveOptions must be a comma-separated list of: {string.Join(", ", ValidSaveOptions)}");
    }

    private static bool BeValidSaveOptions(string saveOptions)
    {
        if (string.IsNullOrWhiteSpace(saveOptions)) return true;
        return saveOptions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(o => ValidSaveOptions.Contains(o));
    }
}
