using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Tables.Commands.SetTablesShowInBar;

/// <summary>Shows/hides one or many tables in the nav bar with a single UPDATE. Replaces the old
/// frontend flow of GET /tables/{id} + PATCH /tables/{id} per table (the full update endpoint rewrites
/// name/labels/description/icon unconditionally, which is why the frontend had to fetch first). Only
/// the IsShowInBar column is written, and only on rows that actually change.</summary>
public class SetTablesShowInBarCommandHandler
{
    private const int MaxBatchSize = 100;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppRepository _appRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppAccessService? _access;

    public SetTablesShowInBarCommandHandler(IAppTableRepository tableRepo, IAppRepository appRepo, IAuditRepository auditRepo,
        IAppAccessService? access = null)
    {
        _access = access;
        _tableRepo = tableRepo;
        _appRepo = appRepo;
        _auditRepo = auditRepo;
    }

    /// <returns>The number of tables whose value actually changed.</returns>
    public async Task<int> HandleAsync(SetTablesShowInBarCommand command, CancellationToken ct = default)
    {
        if (command.PublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["publicIds"] = ["At least one table ID is required."] });
        if (command.PublicIds.Count > MaxBatchSize)
            throw new ValidationException(new Dictionary<string, string[]> { ["publicIds"] = [$"Cannot update more than {MaxBatchSize} tables at once."] });

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);
        if (appId == 0)
            throw new NotFoundException("App", command.AppPublicId);

        // [RequireAppPermission] only verified TablesUpdate on the app in the URL, so the repository
        // scopes the UPDATE to that app - ids belonging to any other app are silently ignored.
        // A role may hold Update Tables on some tables only (Roles > Advanced Permissions > Table selection): every table
        // of this app in the request must allow it, or the whole request is refused (nothing is half-changed).
        var ids = command.PublicIds.Distinct().ToList();
        if (_access is not null)
        {
            foreach (var publicId in ids)
            {
                var table = await _tableRepo.GetByPublicIdAsync(publicId, ct);
                if (table.AppId != appId) continue;
                await _access.RequirePermissionByTablePublicIdAsync(publicId, PermissionCodes.TablesUpdate, ct);
            }
        }
        var changed = await _tableRepo.SetShowInBarAsync(appId, ids, command.IsShowInBar, ct);

        foreach (var (publicId, _) in changed)
        {
            await _auditRepo.LogActivityAsync(
                AuditActions.Updated, AuditEntityTypes.AppTable, publicId.ToString(),
                $"Table updated: Show In Bar to '{command.IsShowInBar}'", appId: appId, ct: ct);
        }

        return changed.Count;
    }
}
