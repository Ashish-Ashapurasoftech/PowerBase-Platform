using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Records.Queries.GetModifiableRecordIds;

public record GetModifiableRecordIdsQuery(Guid TablePublicId, IReadOnlyList<Guid> RecordPublicIds);

/// <summary>
/// Which of the given records the current user may edit / delete under the role's Modify Custom Rules.
/// Lets the grid and record page hide Edit / Delete for rows the rules exclude — the write endpoints
/// enforce the same rules (RecordModifyGuard), so this only drives the UI.
/// </summary>
public class GetModifiableRecordIdsQueryHandler
{
    public const int MaxIds = 500;

    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly RecordModifyGuard _guard;

    public GetModifiableRecordIdsQueryHandler(
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRolePermissionEnforcer enforcer, RecordModifyGuard guard)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _enforcer = enforcer;
        _guard = guard;
    }

    public async Task<IReadOnlyList<Guid>> HandleAsync(GetModifiableRecordIdsQuery query, CancellationToken ct = default)
    {
        var ids = query.RecordPublicIds.Distinct().Take(MaxIds).ToList();
        if (ids.Count == 0) return ids;

        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);

        if (!access.Unrestricted && access.ModifyScope == RecordScopes.None) return [];
        return await _guard.FilterAllowedAsync(access, table, fields, ids, ct);
    }
}
