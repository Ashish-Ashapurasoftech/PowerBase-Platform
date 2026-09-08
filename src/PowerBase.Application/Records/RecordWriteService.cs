using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.Application.Records;

/// <summary>
/// Applies a set of field writes to a record and audits the before/after diff. Shared by
/// normal record edits (<see cref="Commands.UpdateRecord.UpdateRecordCommandHandler"/>) and
/// privileged Action Button writes (InvokeButtonAction), so both paths get identical
/// reference-value resolution, persistence, and audit-diff behavior.
/// </summary>
public interface IRecordWriteService
{
    /// <summary>Writes <paramref name="fieldValues"/> to the record and logs an audit entry.
    /// Returns the effective values actually persisted (post reference-override resolution),
    /// keyed by Fid — callers can use this to report back what changed.</summary>
    Task<IReadOnlyDictionary<long, object?>> ApplyAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string auditAction,
        string entityTitle,
        CancellationToken ct = default,
        System.Data.IDbTransaction? transaction = null,
        bool suppressInterception = false,
        Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null);
}

public sealed class RecordWriteService : IRecordWriteService
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IPipelineTriggerInterceptor _triggerInterceptor;
    private readonly FormulaEngine _engine;

    public RecordWriteService(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IAppUserRepository appUserRepo,
        IUserRepository userRepo,
        IAuditRepository auditRepo,
        IPipelineTriggerInterceptor triggerInterceptor,
        FormulaEngine engine)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _appUserRepo = appUserRepo;
        _userRepo = userRepo;
        _auditRepo = auditRepo;
        _triggerInterceptor = triggerInterceptor;
        _engine = engine;
    }

    private static bool AreValuesEqual(object? val1, object? val2, string? typeCode)
    {
        if (val1 == null && val2 == null) return true;
        if (val1 == null || val2 == null) return false;

        // Numeric normalization
        if (typeCode == "Number" || typeCode == "Numeric" || typeCode == "Decimal" || typeCode == "Integer" || typeCode == "Int")
        {
            if (decimal.TryParse(val1.ToString(), out var d1) && decimal.TryParse(val2.ToString(), out var d2))
            {
                return d1 == d2;
            }
        }

        // Date/Time normalization
        if (typeCode == "Date" || typeCode == "DateTime" || typeCode == "Time")
        {
            if (DateTime.TryParse(val1.ToString(), out var dt1) && DateTime.TryParse(val2.ToString(), out var dt2))
            {
                return dt1 == dt2;
            }
        }

        // Boolean normalization
        if (typeCode == "Boolean" || typeCode == "Bool")
        {
            if (bool.TryParse(val1.ToString(), out var b1) && bool.TryParse(val2.ToString(), out var b2))
            {
                return b1 == b2;
            }
        }

        return string.Equals(val1.ToString()?.Trim(), val2.ToString()?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<long, object?>> ApplyAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string auditAction,
        string entityTitle,
        CancellationToken ct = default,
        System.Data.IDbTransaction? transaction = null,
        bool suppressInterception = false,
        Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null)
    {
        // Reference fields must point at an existing parent record.
        var refOverrides = await ReferenceWriteValidator.ValidateAsync(fields, fieldValues, _tableRepo, _fieldRepo, _recordRepo, ct);

        // Fetch old values before update so we can diff them
        var oldRecord = await _recordRepo.GetByPublicIdAsync(table, fields, recordPublicId, ct);

        var effectiveValues = new Dictionary<long, object?>(fieldValues);
        foreach (var kvp in refOverrides)
            effectiveValues[kvp.Key] = kvp.Value;

        // User/MultiUser values submitted from the record form's picker (or an Action Button's
        // "Add Data" values) arrive as userPublicId Guid(s) — resolve to the long id the column
        // actually stores. See UserFieldValueResolver's doc comment for why.
        await UserFieldValueResolver.ResolveAsync(_userRepo, fields, effectiveValues, ct);

        // Field-level Required / Unique constraints (Quickbase-style) — checked against the final
        // values about to be persisted, excluding this record itself from the Unique collision check.
        var recordId = Convert.ToInt64(oldRecord["Id"]);
        await RecordConstraintValidator.ValidateAsync(table, fields, effectiveValues, _recordRepo, isCreate: false, excludeRecordId: recordId, ct);

        // Custom Data Rule — same formula-based save gate as record creation (see
        // CreateRecordCommandHandler), covering both plain record edits and Action Button writes
        // that go through this shared service.
        await CustomDataRuleValidator.ValidateAsync(table, fields, effectiveValues, _tableRepo, _fieldRepo, _recordRepo, _engine, ct);

        await _recordRepo.UpdateAsync(table, fields, recordPublicId, effectiveValues, transaction, ct, onIndexMessageCreated);

        // Build before/after values and genuinely changed field IDs keyed by f.Id
        var beforeValues = new Dictionary<long, object?>();
        var afterValues = new Dictionary<long, object?>();
        var changedFieldIds = new List<long>();

        foreach (var f in fields)
        {
            if (f.Fid.HasValue)
            {
                var colKey = PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(f);
                var oldVal = oldRecord.TryGetValue(colKey, out var ov) ? ov : null;
                beforeValues[f.Id] = oldVal;

                if (effectiveValues.TryGetValue(f.Fid.Value, out var newVal))
                {
                    afterValues[f.Id] = newVal;
                    if (!AreValuesEqual(oldVal, newVal, f.TypeCode))
                    {
                        changedFieldIds.Add(f.Id);
                    }
                }
                else
                {
                    afterValues[f.Id] = oldVal;
                }
            }
        }

        // Build field-level diff — only fields where value actually changed, keyed by display label
        var candidateFields = fields.Where(f =>
            f.Fid.HasValue && fieldValues.ContainsKey((long)f.Fid.Value) && !f.IsSystem && f.PhysicalColumnName is not null && f.IsAuditable).ToList();

        var actuallyChanged = candidateFields.Where(f =>
        {
            var colKey = PowerBase.Domain.Constants.PhysicalNaming.ColumnName(f.Fid!.Value);
            var oldVal = oldRecord.TryGetValue(colKey, out var ov) ? ov?.ToString() : null;
            var newVal = fieldValues.TryGetValue((long)f.Fid.Value, out var nv) ? nv?.ToString() : null;
            return oldVal != newVal;
        }).ToList();

        if (actuallyChanged.Count == 0)
        {
            // Nothing really changed — log a simple entry with no diff
            await _auditRepo.LogActivityAsync(
                auditAction, AuditEntityTypes.Record, recordPublicId.ToString(),
                entityTitle,
                appId: table.AppId, ct: ct);
            return effectiveValues;
        }

        // Resolve User-type fields: load app users once if any User field changed
        Dictionary<string, string>? userNameMap = null;
        if (actuallyChanged.Any(f => f.TypeCode == "User"))
        {
            var appUsers = await _appUserRepo.ListByAppIdAsync(table.AppId, ct);
            userNameMap = appUsers.ToDictionary(
                u => u.PublicId.ToString(),
                u => u.UserName,
                StringComparer.OrdinalIgnoreCase);
        }

        string ResolveDisplay(AppField f, string? raw)
        {
            if (raw is null) return string.Empty;
            if (f.TypeCode == "User" && userNameMap is not null && userNameMap.TryGetValue(raw, out var name))
                return name;
            return raw;
        }

        var oldValuesDict = actuallyChanged.ToDictionary(
            f => f.Label ?? f.Name,
            f => ResolveDisplay(f, oldRecord.TryGetValue(PowerBase.Domain.Constants.PhysicalNaming.ColumnName(f.Fid!.Value), out var v) ? v?.ToString() : null)
        );
        var newValuesDict = actuallyChanged.ToDictionary(
            f => f.Label ?? f.Name,
            f => ResolveDisplay(f, fieldValues.TryGetValue((long)f.Fid!.Value, out var v) ? v?.ToString() : null)
        );

        await _auditRepo.LogActivityAsync(
            auditAction, AuditEntityTypes.Record, recordPublicId.ToString(),
            entityTitle,
            appId: table.AppId,
            oldValues: JsonSerializer.Serialize(oldValuesDict),
            newValues: JsonSerializer.Serialize(newValuesDict),
            ct: ct);

        if (!suppressInterception)
        {
            await _triggerInterceptor.InterceptAsync(table, fields, recordPublicId, afterValues, "record-updated", ct, beforeValues, changedFieldIds);
        }

        return effectiveValues;
    }
}
