using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Settings;
using PowerBase.Domain.Constants;
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
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

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

    /// <summary>Validates an ActionButton's configured Capture/Timestamp/Bool-Gate/IP-Capture/
    /// Location/Add-Data/Prompt-Source Fid references against the table's actual fields: each must
    /// exist, must not be a system or computed field (the same rule InvokeButtonActionCommandHandler
    /// enforces as "defense in depth" at click time — this catches it at save time instead so a
    /// misconfiguration surfaces immediately rather than on first use), and must be one of the
    /// TypeCodes that slot can actually hold (e.g. Timestamp Field must be Date/DateTime, not
    /// Number — mirrors the frontend's ActionButtonSettingsPanelComponent field pickers). A no-op
    /// for every other field type or malformed/absent Settings (already rejected elsewhere by the
    /// shape-only ActionButtonSettingsValidator). Throws ValidationException.</summary>
    public void ValidateActionButtonTargets(
        string typeCode, string? settings, IReadOnlyList<AppField> tableFields, long? selfFieldId)
    {
        if (!PhysicalNaming.IsActionButtonTypeCode(typeCode) || string.IsNullOrWhiteSpace(settings))
            return;

        ActionButtonSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ActionButtonSettings>(settings, JsonOpts);
        }
        catch (JsonException)
        {
            return;
        }
        if (parsed is null) return;

        var byFid = tableFields.Where(f => f.Fid.HasValue).ToDictionary(f => f.Fid!.Value);
        var errors = new Dictionary<string, string[]>();

        void CheckTarget(string field, int? fid, string[]? allowedTypeCodes)
        {
            if (fid is not int f) return;
            if (f == selfFieldId)
            {
                errors[field] = ["An Action Button cannot target itself."];
                return;
            }
            if (!byFid.TryGetValue(f, out var target))
            {
                errors[field] = [$"Field {f} does not exist on this table."];
                return;
            }
            if (target.IsSystem || PhysicalNaming.IsComputedTypeCode(target.TypeCode))
            {
                errors[field] = [$"'{target.Label ?? target.Name}' is a system/computed field and cannot be used here."];
                return;
            }
            if (allowedTypeCodes is not null && !allowedTypeCodes.Contains(target.TypeCode))
            {
                errors[field] = [$"'{target.Label ?? target.Name}' is not a supported field type for this setting " +
                    $"(expected {string.Join("/", allowedTypeCodes)})."];
            }
        }

        var captureAllowed = parsed.Variant is ActionButtonVariants.Signature or ActionButtonVariants.File
            ? new[] { "File" }
            : null; // Prompt: the answer can reasonably land in many field types.
        CheckTarget("Settings.CaptureFid", parsed.CaptureFid, captureAllowed);
        CheckTarget("Settings.TimestampFid", parsed.TimestampFid, ["Date", "DateTime"]);
        CheckTarget("Settings.BoolGateFid", parsed.BoolGateFid, ["Boolean"]);
        CheckTarget("Settings.IpCaptureFid", parsed.IpCaptureFid, ["Text"]);
        CheckTarget("Settings.LocationCapture.TargetFid", parsed.LocationCapture?.TargetFid, ["Text"]);
        CheckTarget("Settings.PromptSourceFid", parsed.PromptSourceFid, ["SingleSelect", "MultiSelect"]);

        if (parsed.AddData is { Length: > 0 })
        {
            for (var i = 0; i < parsed.AddData.Length; i++)
                CheckTarget($"Settings.AddData[{i}].TargetFid", parsed.AddData[i].TargetFid, null);
        }

        if (errors.Count > 0)
            throw new ValidationException(errors.AsReadOnly());
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
