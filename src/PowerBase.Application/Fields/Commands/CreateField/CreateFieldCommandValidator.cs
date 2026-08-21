using FluentValidation;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Commands.CreateField;

public class CreateFieldCommandValidator : AbstractValidator<CreateFieldCommand>
{
    public CreateFieldCommandValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(200);
        // TypeCode's real whitelist is this tenant's own core.FieldType table, not a fixed set —
        // different tenant databases can (and do) carry different rows there (see
        // PhysicalNaming.IsActionButtonTypeCode's comment for why). A prior version of this rule
        // checked TypeCode against the static PowerBase.Domain.Enums.FieldTypeCode enum instead,
        // which rejected perfectly valid tenant-DB codes (e.g. 'ActionButton_Signature') before
        // the request ever reached the actual, tenant-DB-backed check —
        // CreateFieldCommandHandler/BulkCreateFieldsCommandHandler's own
        // `_fieldTypeRepo.GetByCodeAsync(command.TypeCode, ct) ?? throw NotFoundException(...)`
        // a few lines later, which is the authoritative, already-correct source of truth for
        // "does this TypeCode exist." Only check non-emptiness here; let that DB lookup do the
        // real validation.
        RuleFor(x => x.TypeCode).NotEmpty();
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
