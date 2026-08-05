using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

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
        CancellationToken ct = default);
}

public sealed class RecordWriteService : IRecordWriteService
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAuditRepository _auditRepo;

    public RecordWriteService(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IAppUserRepository appUserRepo,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _appUserRepo = appUserRepo;
        _auditRepo = auditRepo;
    }

    public async Task<IReadOnlyDictionary<long, object?>> ApplyAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string auditAction,
        string entityTitle,
        CancellationToken ct = default)
    {
        // Reference fields must point at an existing parent record.
        var refOverrides = await ReferenceWriteValidator.ValidateAsync(fields, fieldValues, _tableRepo, _fieldRepo, _recordRepo, ct);

        // Fetch old values before update so we can diff them
        var oldRecord = await _recordRepo.GetByPublicIdAsync(table, fields, recordPublicId, ct);

        var effectiveValues = new Dictionary<long, object?>(fieldValues);
        foreach (var kvp in refOverrides)
            effectiveValues[kvp.Key] = kvp.Value;

        await _recordRepo.UpdateAsync(table, fields, recordPublicId, effectiveValues, ct);

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

        return effectiveValues;
    }
}
