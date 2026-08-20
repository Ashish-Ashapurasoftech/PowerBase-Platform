using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tables.Commands.BulkDeleteTables;

/// <summary>Deletes several tables from a single HTTP request. Table deletion isn't a plain
/// "UPDATE ... WHERE Id IN (...)" — DeleteTableCommandHandler also force-deletes each table's
/// relationships (which can revert/soft-delete fields on the OTHER side of the relationship too).
/// Re-implementing that cascade as raw bulk SQL here would duplicate — and risk drifting from —
/// that logic, so this loops over the existing single-table handler instead. The frontend still
/// only makes one call; this is what turns it into N server-side deletes, mirroring how
/// BulkDeleteAppsCommandHandler reuses per-item repository calls rather than a single statement.</summary>
public class BulkDeleteTablesCommandHandler
{
    private const int MaxBatchSize = 100;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppRepository _appRepo;
    private readonly DeleteTableCommandHandler _deleteTableHandler;

    public BulkDeleteTablesCommandHandler(
        IAppTableRepository tableRepo,
        IAppRepository appRepo,
        DeleteTableCommandHandler deleteTableHandler)
    {
        _tableRepo = tableRepo;
        _appRepo = appRepo;
        _deleteTableHandler = deleteTableHandler;
    }

    public async Task<int> HandleAsync(BulkDeleteTablesCommand command, CancellationToken ct = default)
    {
        if (command.PublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["publicIds"] = ["At least one table ID is required."] });
        if (command.PublicIds.Count > MaxBatchSize)
            throw new ValidationException(new Dictionary<string, string[]> { ["publicIds"] = [$"Cannot delete more than {MaxBatchSize} tables at once."] });

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);
        if (appId == 0)
        {
            throw new NotFoundException("App", command.AppPublicId);
        }

        var deletedCount = 0;
        foreach (var publicId in command.PublicIds)
        {
            var table = await _tableRepo.GetByPublicIdAsync(publicId, ct);

            // [RequireAppPermission] on the controller only verified TablesDelete on
            // command.AppPublicId — not on whichever app each individual table id actually
            // belongs to. Silently skip anything outside that app rather than deleting it.
            if (table.AppId != appId) continue;

            await _deleteTableHandler.HandleAsync(new DeleteTableCommand(publicId), ct);
            deletedCount++;
        }

        return deletedCount;
    }
}
