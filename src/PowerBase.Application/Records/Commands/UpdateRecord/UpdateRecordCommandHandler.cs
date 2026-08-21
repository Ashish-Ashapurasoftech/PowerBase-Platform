using PowerBase.Application.Common.Interfaces;
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

    public UpdateRecordCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRolePermissionEnforcer enforcer,
        IRecordWriteService writeService,
        ITenantUnitOfWork uow)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _enforcer = enforcer;
        _writeService = writeService;
        _uow = uow;
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
            var blocked = command.FieldValues.Keys.Where(k => !access.EditableFieldIds.Contains(k)).ToList();
            if (blocked.Count > 0)
                throw new UnauthorizedActionException("You do not have permission to write to one or more of the specified fields.");
        }

        await _uow.BeginAsync(ct);
        try
        {
            await _writeService.ApplyAsync(
                table, fields, command.RecordPublicId, command.FieldValues,
                AuditActions.Updated, $"Record modified in {table.Name}", ct, _uow.Transaction);
            await _uow.CommitAsync(ct);
        }
        catch
        {
            await _uow.RollbackAsync(ct);
            throw;
        }
    }
}
