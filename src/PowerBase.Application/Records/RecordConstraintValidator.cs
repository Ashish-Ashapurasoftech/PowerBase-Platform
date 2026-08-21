using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records;

/// <summary>
/// Enforces field-level Required and Unique constraints at record write time, mirroring Quickbase's
/// behavior: a required field can't be left blank, and a unique field can't collide with another
/// record's value. Runs against the effective (post-default, post-reference-override) values a write
/// is about to persist — shared by CreateRecordCommandHandler, RecordWriteService (record edits +
/// Action Button writes), and MassUpdateRecordsCommandHandler so every write path enforces identical
/// rules against the same source of truth (meta.AppField.IsRequired/IsUnique) — never hard-coded.
/// </summary>
public static class RecordConstraintValidator
{
    /// <param name="isCreate">On create, a Required field missing from <paramref name="effectiveValues"/>
    /// entirely (no submitted value, no default) is itself a violation. On update, only fields actually
    /// present in the write are checked — untouched fields are left alone.</param>
    /// <param name="excludeRecordId">The record's internal row Id, excluded from Unique collision checks
    /// so a record doesn't collide with its own current value. Null on create.</param>
    public static async Task ValidateAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IRecordRepository recordRepo,
        bool isCreate,
        long? excludeRecordId,
        CancellationToken ct)
    {
        var violations = await CollectViolationsAsync(table, fields, effectiveValues, recordRepo, isCreate, excludeRecordId, ct);
        if (violations.Count > 0)
            throw new ValidationException(violations.ToDictionary(v => v.FieldId.ToString(), v => new[] { v.Message }));
    }

    /// <summary>Same rules as <see cref="ValidateAsync"/>, but collects every violation instead of
    /// throwing on the first — used by callers that need to validate several records before deciding
    /// whether to write anything (e.g. mass update's all-or-nothing pre-flight).</summary>
    /// <param name="recordId">Echoed back on each violation for the caller's own reporting; not used
    /// for lookup. Defaults to <see cref="Guid.Empty"/> for single-record callers that don't need it.</param>
    public static async Task<List<RecordConstraintViolation>> CollectViolationsAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IRecordRepository recordRepo,
        bool isCreate,
        long? excludeRecordId,
        CancellationToken ct,
        Guid recordId = default)
    {
        var violations = new List<RecordConstraintViolation>();

        foreach (var field in fields)
        {
            if (field.IsSystem || !field.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(field.TypeCode))
                continue;

            var fid = (long)field.Fid.Value;
            var label = field.Label ?? field.Name;

            if (!effectiveValues.TryGetValue(fid, out var value))
            {
                if (isCreate && field.IsRequired)
                    violations.Add(new RecordConstraintViolation(recordId, fid, "Required", $"'{label}' is required."));
                continue;
            }

            var isBlank = value is null || (value is string s && string.IsNullOrWhiteSpace(s));

            if (field.IsRequired && isBlank)
            {
                violations.Add(new RecordConstraintViolation(recordId, fid, "Required", $"'{label}' is required."));
                continue; // No point checking uniqueness of a value we just rejected as blank.
            }

            if (field.IsUnique && !isBlank && !PhysicalNaming.IsRangeTypeCode(field.TypeCode))
            {
                if (await recordRepo.HasValueDuplicateAsync(table, field, value!, excludeRecordId, ct))
                    violations.Add(new RecordConstraintViolation(recordId, fid, "Unique", $"'{label}' must be unique — this value is already in use."));
            }
        }

        return violations;
    }
}
