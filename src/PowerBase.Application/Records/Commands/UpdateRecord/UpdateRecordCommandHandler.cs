using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Records.Commands.UpdateRecord;

public class UpdateRecordCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IRecordWriteService _writeService;
    private readonly ITenantUnitOfWork _uow;
    private readonly IMessagePublisher _messagePublisher;
    private readonly IReferenceRelinkService? _relink;
    private readonly RecordModifyGuard? _modifyGuard;
    private readonly ILogger<UpdateRecordCommandHandler>? _logger;

    public UpdateRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRolePermissionEnforcer enforcer,
        IRecordWriteService writeService,
        ITenantUnitOfWork uow,
        IMessagePublisher messagePublisher,
        IReferenceRelinkService? relink = null,
        RecordModifyGuard? modifyGuard = null,
        ILogger<UpdateRecordCommandHandler>? logger = null)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _enforcer = enforcer;
        _writeService = writeService;
        _uow = uow;
        _messagePublisher = messagePublisher;
        _relink = relink;
        _modifyGuard = modifyGuard;
        _logger = logger;
    }

    public async Task HandleAsync(UpdateRecordCommand command, CancellationToken ct = default)
    {
        if (command.FieldValues.Count == 0)
            return;

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var tableFieldIds = new HashSet<long>(fields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value));
        var unknownIds = command.FieldValues.Keys.Where(k => !tableFieldIds.Contains(k)).ToList();
        if (unknownIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Unknown field IDs: {string.Join(", ", unknownIds)}"] });

        var computedIds = command.FieldValues.Keys
            .Where(k => fields.Any(f => f.Fid.HasValue && (long)f.Fid.Value == k && PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
            .ToList();
        if (computedIds.Count > 0)
            throw new ValidationException(
                new Dictionary<string, string[]> { ["fields"] = [$"Formula fields are read-only and cannot be set: {string.Join(", ", computedIds)}"] });

        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted)
        {
            if (access.ModifyScope == RecordScopes.None)
                throw new UnauthorizedActionException("You do not have permission to edit records in this table.");
            if (access.ViewScope == RecordScopes.OwnRecords || access.ModifyScope == RecordScopes.OwnRecords)
                await _enforcer.EnsureRecordOwnedAsync(table, command.RecordPublicId, ct);
            if (_modifyGuard is not null)
                await _modifyGuard.EnsureAsync(access, table, fields, [command.RecordPublicId], ct);
            var blocked = command.FieldValues.Keys.Where(k => !access.EditableFieldIds.Contains(k)).ToList();
            if (blocked.Count > 0)
                throw new UnauthorizedActionException("You do not have permission to write to one or more of the specified fields.");
        }

        PowerBase.Application.Common.Models.SearchIndexMessage? indexMessage = null;
        IReadOnlyDictionary<long, object?> written = command.FieldValues;

        await _uow.BeginAsync(ct);
        try
        {
            // The record's key before this write (only read when the write sets a key some child keeps unmatched text for),
            // so that a change of it can queue the background job that links children whose text is its NEW key.
            var keyBefore = _relink is null ? null
                : await _relink.SnapshotAsync(table, fields, command.RecordPublicId, command.FieldValues.Keys, _uow.Transaction, ct);

            var writesFileField = fields.Any(field => field.Fid.HasValue &&
                command.FieldValues.ContainsKey(field.Fid.Value) &&
                string.Equals(field.TypeCode, "File", StringComparison.OrdinalIgnoreCase));
            if (writesFileField)
            {
                if (_writeService is IFileRecordWriteService fileWriteService)
                {
                    written = await fileWriteService.ApplyFileWriteAsync(
                        table, fields, command.RecordPublicId, command.FieldValues,
                        AuditActions.Updated, $"Record modified in {table.Name}", ct, _uow.Transaction, false,
                        msg => indexMessage = msg, existingRecord: null, bypassFileReservation: false,
                        reportId: command.ReportId, isGridEditSave: command.IsGridEditSave);
                }
                else
                {
                    written = await _writeService.ApplyAsync(
                        table, fields, command.RecordPublicId, command.FieldValues,
                        AuditActions.Updated, $"Record modified in {table.Name}", ct, _uow.Transaction, false,
                        msg => indexMessage = msg, existingRecord: null, reportId: command.ReportId,
                        isGridEditSave: command.IsGridEditSave);
                }
            }
            else
            {
                written = await _writeService.ApplyAsync(
                    table, fields, command.RecordPublicId, command.FieldValues,
                    AuditActions.Updated, $"Record modified in {table.Name}", ct, _uow.Transaction, false,
                    msg => indexMessage = msg, existingRecord: null, reportId: command.ReportId,
                    isGridEditSave: command.IsGridEditSave);
            }

            // Queued now, run once this save commits (nothing is queued if it rolls back).
            if (keyBefore is not null)
                await _relink!.QueueIfKeyChangedAsync(table, [keyBefore], written, ct);

            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (indexMessage != null)
        {
            _ = _messagePublisher.PublishPriorityAsync(indexMessage, default).ContinueWith(
                t => _logger?.LogWarning(t.Exception, "Failed to immediately publish priority search index message for record {RecordId} in table {TableId}. SearchOutbox will relay.", command.RecordPublicId, table.Id),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
