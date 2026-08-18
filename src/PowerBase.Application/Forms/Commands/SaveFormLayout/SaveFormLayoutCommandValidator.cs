using FluentValidation;

namespace PowerBase.Application.Forms.Commands.SaveFormLayout;

public class SaveFormLayoutCommandValidator : AbstractValidator<SaveFormLayoutCommand>
{
    private static readonly HashSet<string> ValidLabelModes   = ["Default", "Custom", "Hide"];
    private static readonly HashSet<string> ValidWidthModes   = ["Auto", "Half", "Full", "Fixed"];
    private static readonly HashSet<string> ValidElementTypes = ["Field", "StaticText", "Divider", "Button", "Report", "Spacer"];
    private static readonly System.Text.RegularExpressions.Regex HexColorRegex =
        new(@"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public SaveFormLayoutCommandValidator()
    {
        RuleFor(x => x.Sections).NotNull();

        RuleForEach(x => x.Pages).ChildRules(page =>
        {
            page.RuleFor(p => p.Heading).NotEmpty().MaximumLength(200);
        });

        RuleForEach(x => x.Sections).ChildRules(section =>
        {
            section.RuleFor(s => s.Name).NotEmpty().MaximumLength(200);

            section.RuleFor(s => s.Blocks)
                .NotNull()
                .Must(b => b.Count is >= 1 and <= 5)
                .WithMessage("Each section must have between 1 and 5 blocks.");

            // GridCols defaults to 12 client-side; a payload that explicitly sends something
            // absurd is still worth rejecting rather than silently producing an unrenderable grid.
            section.RuleFor(s => s.GridCols).InclusiveBetween(1, 24).When(s => s.GridCols.HasValue)
                .WithMessage("GridCols must be between 1 and 24.");

            section.RuleForEach(s => s.Blocks).ChildRules(block =>
            {
                block.RuleFor(b => b.Heading).MaximumLength(200).When(b => b.Heading != null);
                block.RuleFor(b => b.Width).GreaterThan(0).When(b => b.Width.HasValue)
                    .WithMessage("Block Width must be a positive integer.");
                block.RuleFor(b => b.BackgroundColor)
                    .Must(c => c == null || HexColorRegex.IsMatch(c))
                    .WithMessage("BackgroundColor must be a valid hex colour (e.g. #RRGGBB).");
                // A zone's grid box, when placed, must fit inside its section's own GridCols —
                // checked per-section below since GridCols lives one level up.
                block.RuleFor(b => b.ColSpan).GreaterThan(0).When(b => b.ColSpan.HasValue)
                    .WithMessage("Block ColSpan must be a positive integer.");
                block.RuleFor(b => b.ColStart).GreaterThan(0).When(b => b.ColStart.HasValue)
                    .WithMessage("Block ColStart must be a positive integer.");

                block.RuleForEach(b => b.Elements).ChildRules(element =>
                {
                    element.RuleFor(e => e.ElementType)
                        .Must(t => ValidElementTypes.Contains(t))
                        .WithMessage("ElementType must be Field, StaticText, Divider, Button, Report, or Spacer.");
                    element.RuleFor(e => e.AppFieldId)
                        .GreaterThan(0)
                        .When(e => e.ElementType == "Field")
                        .WithMessage("AppFieldId is required for Field elements.");
                    element.RuleFor(e => e.LabelMode)
                        .Must(m => ValidLabelModes.Contains(m))
                        .WithMessage("LabelMode must be Default, Custom, or Hide.");
                    element.RuleFor(e => e.WidthMode)
                        .Must(m => ValidWidthModes.Contains(m))
                        .WithMessage("WidthMode must be Auto, Half, Full, or Fixed.");
                    element.RuleFor(e => e.ColSpan).GreaterThan(0).When(e => e.ColSpan.HasValue)
                        .WithMessage("Element ColSpan must be a positive integer.");
                    element.RuleFor(e => e.ColStart).GreaterThan(0).When(e => e.ColStart.HasValue)
                        .WithMessage("Element ColStart must be a positive integer.");
                    element.RuleFor(e => e.RowStart).GreaterThan(0).When(e => e.RowStart.HasValue)
                        .WithMessage("Element RowStart must be a positive integer.");
                    element.RuleFor(e => e.RowSpan).GreaterThan(0).When(e => e.RowSpan.HasValue)
                        .WithMessage("Element RowSpan must be a positive integer.");
                });
            });

            // Placed (ColStart+ColSpan set) zones/elements must stay within the section's own
            // GridCols — this is the check that stops a malformed payload from persisting a
            // layout that can never render as a valid CSS Grid.
            section.RuleFor(s => s).Must(s =>
            {
                var gridCols = s.GridCols ?? 12;
                var zonesFit = s.Blocks.All(b => !b.ColStart.HasValue || !b.ColSpan.HasValue || b.ColStart.Value + b.ColSpan.Value - 1 <= gridCols);
                var elementsFit = s.Blocks.SelectMany(b => b.Elements)
                    .All(e => !e.ColStart.HasValue || !e.ColSpan.HasValue || e.ColStart.Value + e.ColSpan.Value - 1 <= gridCols);
                return zonesFit && elementsFit;
            }).WithMessage("A block or element's ColStart + ColSpan exceeds the section's GridCols.");
        });
    }
}
