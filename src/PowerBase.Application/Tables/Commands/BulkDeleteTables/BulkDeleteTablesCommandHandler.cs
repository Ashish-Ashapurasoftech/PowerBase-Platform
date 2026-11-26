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
    private readonly IAppAccessService? _access;

    public BulkDeleteTablesCommandHandler(
        IAppTableRepository tableRepo,
        IAppRepository appRepo,
        DeleteTableCommandHandler deleteTableHandler,
        IAppAccessService? access = null)
    {
        _access = access;
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

        // [RequireAppPermission] on the controller only verified TablesDelete on command.AppPublicId — not on whichever
        // app each table belongs to (anything outside that app is silently skipped), nor on each table: a role may hold
        // Delete Tables on some tables only (Roles > Advanced Permissions > Table selection). Check every table FIRST and
        // refuse the whole request if one is not allowed, so nothing is half-deleted.
        var targets = new List<Guid>();
        foreach (var publicId in command.PublicIds.Distinct())
        {
            var table = await _tableRepo.GetByPublicIdAsync(publicId, ct);
            if (table.AppId != appId) continue;
            if (_access is not null)
                await _access.RequirePermissionByTablePublicIdAsync(publicId, PowerBase.Domain.Constants.PermissionCodes.TablesDelete, ct);
            targets.Add(publicId);
        }

        var deletedCount = 0;
        foreach (var publicId in targets)
        {
            await _deleteTableHandler.HandleAsync(new DeleteTableCommand(publicId), ct);
            deletedCount++;
        }

        return deletedCount;
    }
}
