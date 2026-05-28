using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Fields.Commands.BulkDeleteFields;

public class BulkDeleteFieldsCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAppAccessService _appAccessService;

    public BulkDeleteFieldsCommandHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IAppAccessService appAccessService)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _appAccessService = appAccessService;
    }

    public async Task HandleAsync(BulkDeleteFieldsCommand command, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);
        
        // System fields protection is handled in the SQL query directly (IsSystem = 0)
        // We just execute the bulk delete.
        await _fieldRepo.BulkDeleteAsync(command.FieldPublicIds, table.Id, ct);
    }
}
