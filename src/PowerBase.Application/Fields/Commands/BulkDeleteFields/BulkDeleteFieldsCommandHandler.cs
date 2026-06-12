using PowerBase.Application.Common.Interfaces;
    using PowerBase.Domain.Constants;

namespace PowerBase.Application.Fields.Commands.BulkDeleteFields;

public class BulkDeleteFieldsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;

    public BulkDeleteFieldsCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAppAccessService appAccessService,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(BulkDeleteFieldsCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        
        // System fields protection is handled in the SQL query directly (IsSystem = 0)
        // We just execute the bulk delete.
        var deletedCount = await _fieldRepo.BulkDeleteAsync(command.FieldPublicIds, table.Id, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged,
            AuditEntityTypes.AppField,
            table.PublicId.ToString(),
            $"{deletedCount} field(s) bulk-deleted from table {table.Name}",
            appId: table.AppId,
            ct: ct);
    }
}
