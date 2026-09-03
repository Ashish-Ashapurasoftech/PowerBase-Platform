using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.Common;

/// <summary>The field-settings validation invariants shared by every flow that can change a
/// field's live configuration — today UpdateFieldCommandHandler and RestoreFieldVersionCommandHandler.
/// Centralized here so a Restore is held to exactly the same rules as a normal Update (requirement:
/// "Validate the resulting configuration using the existing field validation rules") instead of a
/// second, drifting copy of these checks.</summary>
public class FieldSettingsGuard
{
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly FieldSettingsValidatorRegistry _settingsRegistry;

    public FieldSettingsGuard(
        IAppRolePermissionRepository permRepo,
        IRecordRepository recordRepo,
        FieldSettingsValidatorRegistry settingsRegistry)
    {
        _permRepo = permRepo;
        _recordRepo = recordRepo;
        _settingsRegistry = settingsRegistry;
    }

    /// <summary>Validates the per-type Settings JSON shape and the General-settings capability
    /// matrix (Required/Unique/Default Value per field type). Throws ValidationException.
    /// <paramref name="capabilitySettings"/> is the Settings value the capability matrix should
    /// read — callers pass <c>settings ?? existing.Settings</c> so an update that omits Settings
    /// entirely still validates against the field's current shape.</summary>
    public void ValidateSettingsAndCapabilities(
        string typeCode, string? settings, string? capabilitySettings,
        string label, bool isRequired, bool isUnique, string? defaultValue)
    {
        var settingsErrors = _settingsRegistry.Validate(typeCode, settings);
        if (settingsErrors.Count > 0)
            throw new ValidationException(settingsErrors.AsReadOnly());

        var capErrors = FieldGeneralSettingsCapability.Validate(typeCode, capabilitySettings, label, isRequired, isUnique, defaultValue);
        if (capErrors.Count > 0)
            throw new ValidationException(capErrors.AsReadOnly());
    }

    /// <summary>A required field with no default value cannot be saved while some role has it set
    /// to None access — those users would never be able to create a record.</summary>
    public async Task ValidateRequiredHasDefaultOrNoRestrictedRolesAsync(
        long fieldId, string label, bool isRequired, string? defaultValue, CancellationToken ct)
    {
        if (!isRequired || !string.IsNullOrWhiteSpace(defaultValue)) return;

        var rolesWithNone = await _permRepo.CountRolesWithNoneAccessForFieldAsync(fieldId, ct);
        if (rolesWithNone > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["DefaultValue"] =
                [
                    $"'{label}' is required but does not have a default value. Because some users are not " +
                    $"allowed to modify '{label}', those users will not be able to add new records. " +
                    "Supply a default value or uncheck Required."
                ],
            });
        }
    }

    /// <summary>Turning Unique on is rejected if duplicate values already exist in the table.</summary>
    public async Task ValidateUniqueTransitionAsync(AppTable table, AppField existing, string label, bool isUnique, CancellationToken ct)
    {
        if (!isUnique || existing.IsUnique) return;

        if (await _recordRepo.HasDuplicatesAsync(table, existing, ct))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["IsUnique"] = [$"Cannot make '{label}' unique — duplicate values already exist. Remove duplicates first."]
            });
        }
    }

    /// <summary>Encryption can only be toggled (either direction) while the table has zero records —
    /// otherwise existing plaintext/ciphertext data would become unreadable.</summary>
    public async Task ValidateEncryptionTransitionAsync(AppTable table, AppField existing, bool isEncrypted, CancellationToken ct)
    {
        if (existing.IsEncrypted == isEncrypted) return;

        var recordCount = await _recordRepo.CountAsync(table, Array.Empty<AppField>(), ct: ct);
        if (recordCount > 0)
        {
            var errorMsg = existing.IsEncrypted
                ? "A field that has been encrypted cannot be un-encrypted if the table has existing records."
                : "Encryption can only be enabled when creating a new field or if the table has no records.";

            throw new ValidationException(new Dictionary<string, string[]> { ["IsEncrypted"] = [errorMsg] });
        }
    }
}
