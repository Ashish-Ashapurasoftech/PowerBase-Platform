using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.UpdateRecord;

public class UpdateRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IAuditRepository _auditRepo;

    public UpdateRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IAppUserRepository appUserRepo,
        IRolePermissionEnforcer enforcer,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _appUserRepo = appUserRepo;
        _enforcer = enforcer;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateRecordCommand command, CancellationToken ct = default)
    {
        if (command.FieldValues.Count == 0)
            return;

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var tableFieldIds = new HashSet<long>(fields.Select(f => f.Id));
        var unknownIds = command.FieldValues.Keys.Where(k => !tableFieldIds.Contains(k)).ToList();
        if (unknownIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Unknown field IDs: {string.Join(", ", unknownIds)}"] });

        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (access.ModifyScope == RecordScopes.None)
                throw new UnauthorizedActionException("You do not have permission to edit records in this table.");
            if (access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords)
                await _enforcer.EnsureRecordOwnedAsync(table, command.RecordPublicId, ct);
            var blocked = command.FieldValues.Keys.Where(k => !access.EditableFieldIds.Contains(k)).ToList();
            if (blocked.Count > 0)
                throw new UnauthorizedActionException("You do not have permission to write to one or more of the specified fields.");
        }

        // Fetch old values before update so we can diff them
        var oldRecord = await _recordRepo.GetByPublicIdAsync(table, fields, command.RecordPublicId, ct);

        await _recordRepo.UpdateAsync(table, fields, command.RecordPublicId, command.FieldValues, ct);

        // Build field-level diff — only fields where value actually changed, keyed by display label
        var candidateFields = fields.Where(f =>
            command.FieldValues.ContainsKey(f.Id) && !f.IsSystem && f.PhysicalColumnName is not null).ToList();

        var actuallyChanged = candidateFields.Where(f =>
        {
            var oldVal = oldRecord.TryGetValue(f.PhysicalColumnName!, out var ov) ? ov?.ToString() : null;
            var newVal = command.FieldValues.TryGetValue(f.Id, out var nv) ? nv?.ToString() : null;
            return oldVal != newVal;
        }).ToList();

        if (actuallyChanged.Count == 0)
        {
            // Nothing really changed — log a simple entry with no diff
            await _auditRepo.LogActivityAsync(
                AuditActions.Updated, AuditEntityTypes.Record, command.RecordPublicId.ToString(),
                $"Record modified in {table.Name}",
                appId: table.AppId, ct: ct);
            return;
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
            f => ResolveDisplay(f, oldRecord.TryGetValue(f.PhysicalColumnName!, out var v) ? v?.ToString() : null)
        );
        var newValuesDict = actuallyChanged.ToDictionary(
            f => f.Label ?? f.Name,
            f => ResolveDisplay(f, command.FieldValues.TryGetValue(f.Id, out var v) ? v?.ToString() : null)
        );

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.Record, command.RecordPublicId.ToString(),
            $"Record modified in {table.Name}",
            appId: table.AppId,
            oldValues: JsonSerializer.Serialize(oldValuesDict),
            newValues: JsonSerializer.Serialize(newValuesDict),
            ct: ct);
    }
}
