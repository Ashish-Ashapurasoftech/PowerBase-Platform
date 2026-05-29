using FluentValidation;

namespace PowerBase.Application.Forms.Commands.SaveFormLayout;

public class SaveFormLayoutCommandValidator : AbstractValidator<SaveFormLayoutCommand>
{
    private static readonly HashSet<string> ValidLabelModes   = ["Default", "Custom", "Hide"];
    private static readonly HashSet<string> ValidWidthModes   = ["Auto", "Half", "Full", "Fixed"];
    private static readonly HashSet<string> ValidElementTypes = ["Field", "StaticText", "Divider", "Button"];
    private static readonly System.Text.RegularExpressions.Regex HexColorRegex =
        new(@"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public SaveFormLayoutCommandValidator()
    {
        RuleFor(x => x.Sections).NotNull();

        RuleForEach(x => x.Sections).ChildRules(section =>
        {
            section.RuleFor(s => s.Name).NotEmpty().MaximumLength(200);

            section.RuleFor(s => s.Blocks)
                .NotNull()
                .Must(b => b.Count is >= 1 and <= 5)
                .WithMessage("Each section must have between 1 and 5 blocks.");

            section.RuleForEach(s => s.Blocks).ChildRules(block =>
            {
                block.RuleFor(b => b.Heading).MaximumLength(200).When(b => b.Heading != null);
                block.RuleFor(b => b.Width).GreaterThan(0).When(b => b.Width.HasValue)
                    .WithMessage("Block Width must be a positive integer.");
                block.RuleFor(b => b.BackgroundColor)
                    .Must(c => c == null || HexColorRegex.IsMatch(c))
                    .WithMessage("BackgroundColor must be a valid hex colour (e.g. #RRGGBB).");

                block.RuleForEach(b => b.Elements).ChildRules(element =>
                {
                    element.RuleFor(e => e.ElementType)
                        .Must(t => ValidElementTypes.Contains(t))
                        .WithMessage("ElementType must be Field, StaticText, Divider, or Button.");
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
                });
            });
        });
    }
}
