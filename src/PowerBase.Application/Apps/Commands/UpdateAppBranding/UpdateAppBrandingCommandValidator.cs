using FluentValidation;

namespace PowerBase.Application.Apps.Commands.UpdateAppBranding;

public class UpdateAppBrandingCommandValidator : AbstractValidator<UpdateAppBrandingCommand>
{
    private static readonly HashSet<string> ValidThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "light", "solid", "slate", "universe", "abyss", "glass", "carbon", "midnight",
        "aurora", "frost", "crystal", "amoled", "nord", "sepia", "ocean", "graphite",
    };

    private static readonly HashSet<string> ValidAccents = new(StringComparer.OrdinalIgnoreCase)
    {
        "indigo", "emerald", "cyan", "rose", "amber", "violet", "pink", "sky", "orange", "lime", "custom",
    };

    private static readonly HashSet<string> ValidFontSizes = new(StringComparer.OrdinalIgnoreCase) { "sm", "md", "lg" };
    private static readonly HashSet<string> ValidDensities = new(StringComparer.OrdinalIgnoreCase) { "compact", "comfortable", "spacious" };
    private static readonly HashSet<string> ValidBorderModes = new(StringComparer.OrdinalIgnoreCase) { "horizontal", "full-grid", "borderless" };

    private static readonly HashSet<string> ValidSidebarStyles = new(StringComparer.OrdinalIgnoreCase) { "expanded", "collapsed", "mini", "floating" };
    private static readonly HashSet<string> ValidNavPositions = new(StringComparer.OrdinalIgnoreCase) { "left", "top" };
    private static readonly HashSet<string> ValidContentWidths = new(StringComparer.OrdinalIgnoreCase) { "full", "boxed" };
    private static readonly HashSet<string> ValidPanelStyles = new(StringComparer.OrdinalIgnoreCase) { "rounded", "sharp", "bordered", "shadowed" };
    private static readonly HashSet<string> ValidHeaderBehaviors = new(StringComparer.OrdinalIgnoreCase) { "fixed", "static" };

    public UpdateAppBrandingCommandValidator()
    {
        RuleFor(x => x.AppPublicId).NotEmpty();

        When(x => x.Branding is not null, () =>
        {
            RuleFor(x => x.Branding!.Theme).Must(ValidThemes.Contains).WithMessage("Unknown theme.");
            RuleFor(x => x.Branding!.Accent).Must(ValidAccents.Contains).WithMessage("Unknown accent.");
            RuleFor(x => x.Branding!.FontSize).Must(ValidFontSizes.Contains).WithMessage("Unknown font size.");
            RuleFor(x => x.Branding!.Density).Must(ValidDensities.Contains).WithMessage("Unknown density.");
            RuleFor(x => x.Branding!.BorderMode).Must(ValidBorderModes.Contains).WithMessage("Unknown border mode.");
            RuleFor(x => x.Branding!.PageSize).Must(v => v is 5 or 10 or 25 or 50).WithMessage("Page size must be 5, 10, 25, or 50.");
            RuleFor(x => x.Branding!.BrandColorHex)
                .Matches("^#[0-9a-fA-F]{6}$")
                .When(x => !string.IsNullOrEmpty(x.Branding!.BrandColorHex))
                .WithMessage("Brand color must be a 6-digit hex value, e.g. #0057ff.");
        });

        When(x => x.Layout is not null, () =>
        {
            RuleFor(x => x.Layout!.SidebarStyle).Must(ValidSidebarStyles.Contains).WithMessage("Unknown sidebar style.");
            RuleFor(x => x.Layout!.NavPosition).Must(ValidNavPositions.Contains).WithMessage("Unknown navigation position.");
            RuleFor(x => x.Layout!.ContentWidth).Must(ValidContentWidths.Contains).WithMessage("Unknown content width.");
            RuleFor(x => x.Layout!.PanelStyle).Must(ValidPanelStyles.Contains).WithMessage("Unknown panel style.");
            RuleFor(x => x.Layout!.HeaderBehavior).Must(ValidHeaderBehaviors.Contains).WithMessage("Unknown header behavior.");
        });
    }
}
