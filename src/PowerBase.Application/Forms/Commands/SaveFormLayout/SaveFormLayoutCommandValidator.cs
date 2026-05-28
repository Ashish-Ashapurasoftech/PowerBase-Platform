using FluentValidation;

namespace PowerBase.Application.Forms.Commands.SaveFormLayout;

public class SaveFormLayoutCommandValidator : AbstractValidator<SaveFormLayoutCommand>
{
    private static readonly HashSet<string> ValidLabelModes  = new() { "Default", "Custom", "Hide" };
    private static readonly HashSet<string> ValidWidthModes  = new() { "Auto", "Half", "Full", "Fixed" };
    private static readonly HashSet<string> ValidElementTypes = new() { "Field", "StaticText", "Divider", "Button" };

    public SaveFormLayoutCommandValidator()
    {
        RuleFor(x => x.Sections).NotNull();

        RuleForEach(x => x.Sections).ChildRules(section =>
        {
            section.RuleFor(s => s.Name).NotEmpty().MaximumLength(200);
            section.RuleFor(s => s.ColumnCount).InclusiveBetween(1, 4);

            section.RuleForEach(s => s.Elements).ChildRules(element =>
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
    }
}
