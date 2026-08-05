using FluentValidation;

namespace PowerBase.Application.Auth.Commands.UpdateMyPreferences;

public class UpdateMyPreferencesCommandValidator : AbstractValidator<UpdateMyPreferencesCommand>
{
    private static readonly HashSet<string> ValidThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "light", "solid", "slate", "universe", "abyss", "glass", "carbon", "midnight",
        "aurora", "frost", "crystal", "amoled", "nord", "sepia", "ocean", "graphite",
    };

    private static readonly HashSet<string> ValidAccents = new(StringComparer.OrdinalIgnoreCase)
    {
        "indigo", "emerald", "cyan", "rose", "amber", "violet", "pink", "sky", "orange", "lime",
    };

    private static readonly HashSet<string> ValidFontSizes = new(StringComparer.OrdinalIgnoreCase) { "sm", "md", "lg" };
    private static readonly HashSet<string> ValidDensities = new(StringComparer.OrdinalIgnoreCase) { "compact", "comfortable", "spacious" };
    private static readonly HashSet<string> ValidBorderModes = new(StringComparer.OrdinalIgnoreCase) { "horizontal", "full-grid", "borderless" };

    public UpdateMyPreferencesCommandValidator()
    {
        RuleFor(x => x.Preferences.Theme).Must(v => ValidThemes.Contains(v!))
            .When(x => x.Preferences.Theme is not null).WithMessage("Unknown theme.");
        RuleFor(x => x.Preferences.Accent).Must(v => ValidAccents.Contains(v!))
            .When(x => x.Preferences.Accent is not null).WithMessage("Unknown accent.");
        RuleFor(x => x.Preferences.FontSize).Must(v => ValidFontSizes.Contains(v!))
            .When(x => x.Preferences.FontSize is not null).WithMessage("Unknown font size.");
        RuleFor(x => x.Preferences.Density).Must(v => ValidDensities.Contains(v!))
            .When(x => x.Preferences.Density is not null).WithMessage("Unknown density.");
        RuleFor(x => x.Preferences.BorderMode).Must(v => ValidBorderModes.Contains(v!))
            .When(x => x.Preferences.BorderMode is not null).WithMessage("Unknown border mode.");
        RuleFor(x => x.Preferences.PageSize).Must(v => v is 5 or 10 or 25 or 50)
            .When(x => x.Preferences.PageSize is not null).WithMessage("Page size must be 5, 10, 25, or 50.");
    }
}
