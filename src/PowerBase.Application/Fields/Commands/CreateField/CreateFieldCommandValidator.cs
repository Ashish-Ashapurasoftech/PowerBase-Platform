using FluentValidation;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Enums;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.CreateField;

public class CreateFieldCommandValidator : AbstractValidator<CreateFieldCommand>
{
    private static readonly string[] ValidTypeCodes = Enum.GetNames<FieldTypeCode>();

    public CreateFieldCommandValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TypeCode)
            .NotEmpty()
            .Must(c => ValidTypeCodes.Contains(c))
            .WithMessage($"TypeCode must be one of: {string.Join(", ", ValidTypeCodes)}");
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
    }

    /// <summary>
    /// Validates the Settings JSON against the per-type contract and throws
    /// <see cref="ValidationException"/> if invalid. Called from the handler so
    /// the registry (a scoped service) is available.
    /// </summary>
    public static void ValidateSettings(
        string typeCode,
        string? settingsJson,
        FieldSettingsValidatorRegistry registry)
    {
        var errors = registry.Validate(typeCode, settingsJson);
        if (errors.Count > 0)
            throw new PowerBase.Domain.Exceptions.ValidationException(errors.AsReadOnly());
    }
}
