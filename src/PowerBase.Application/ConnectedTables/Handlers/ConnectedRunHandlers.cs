using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Sync;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.ConnectedTables.Handlers;

/// <summary>Refreshing a connected table and reading its refresh history.</summary>
public sealed class ConnectedRunHandlers(
    IConnectedTableRepository connectedTables,
    IConnectedRunRepository runs,
    ConnectedRunStarter starter,
    ConnectedPermissions permissions,
    IUserRepository users,
    IQueryContext context)
{
    public async Task<ConnectedRunResult> RefreshAsync(Guid publicId, string trigger, CancellationToken ct)
    {
        var table = await GetTableAsync(publicId, ct);
        await permissions.RequireCanRefreshAsync(table, ct);
        var run = await starter.QueueAsync(table, trigger, context.UserId, confirmMassDelete: false, ct);
        return await ResultAsync(run, ct);
    }

    /// <summary>Releases a refresh held by the mass-delete guard: runs it again, this time allowed to remove the records.</summary>
    public async Task<ConnectedRunResult> ConfirmDeletionAsync(Guid publicId, CancellationToken ct)
    {
        var table = await GetTableAsync(publicId, ct);
        await permissions.RequireOwnerOrAdminAsync(table, ct);
        if (table.Status != ConnectedTableStatuses.Held)
            throw ConnectedTableException.Conflict(ConnectedErrorCodes.Forbidden, "No refresh of this table is waiting for confirmation.");
        var run = await starter.QueueAsync(table, ConnectedRunTriggers.Manual, context.UserId, confirmMassDelete: true, ct);
        return await ResultAsync(run, ct);
    }

    public async Task CancelAsync(Guid publicId, CancellationToken ct)
    {
        var table = await GetTableAsync(publicId, ct);
        await permissions.RequireCanRefreshAsync(table, ct);
        var active = await runs.GetActiveForTableAsync(table.Id, ct)
            ?? throw ConnectedTableException.Conflict(ConnectedErrorCodes.Forbidden, "This table is not refreshing.");
        await runs.RequestCancelAsync(active.Id, ct);
    }

    public async Task<IReadOnlyList<ConnectedRunResult>> ListAsync(Guid publicId, int take, CancellationToken ct)
    {
        var table = await GetTableAsync(publicId, ct);
        await permissions.RequireMemberAsync(table.AppId, ct);
        var list = await runs.ListByTableAsync(table.Id, Math.Clamp(take, 1, 200), ct);
        var names = await users.GetNamesByIdsAsync(list.Select(r => r.TriggeredByUserId).Distinct(), ct);
        return list.Select(r => ToResult(r, names)).ToList();
    }

    public async Task<ConnectedRunResult> GetAsync(Guid runPublicId, CancellationToken ct)
    {
        var run = await runs.GetByPublicIdAsync(runPublicId, ct) ?? throw new NotFoundException("ConnectedRun", runPublicId);
        var table = await connectedTables.GetByIdAsync(run.ConnectedTableId, ct) ?? throw new NotFoundException("ConnectedRun", runPublicId);
        await permissions.RequireMemberAsync(table.AppId, ct);
        return await ResultAsync(run, ct);
    }

    public async Task<IReadOnlyList<ConnectedRunIssueResult>> ListIssuesAsync(Guid runPublicId, CancellationToken ct)
    {
        var run = await runs.GetByPublicIdAsync(runPublicId, ct) ?? throw new NotFoundException("ConnectedRun", runPublicId);
        var table = await connectedTables.GetByIdAsync(run.ConnectedTableId, ct) ?? throw new NotFoundException("ConnectedRun", runPublicId);
        await permissions.RequireMemberAsync(table.AppId, ct);
        return (await runs.ListIssuesAsync(run.Id, 1000, ct))
            .Select(i => new ConnectedRunIssueResult(i.SourceRecordId, i.SourceFid, i.Outcome, i.ReasonCode, i.Message)).ToList();
    }

    private async Task<ConnectedRunResult> ResultAsync(ConnectedRun run, CancellationToken ct)
    {
        var fresh = await runs.GetByPublicIdAsync(run.PublicId, ct) ?? run;
        return ToResult(fresh, await users.GetNamesByIdsAsync([fresh.TriggeredByUserId], ct));
    }

    private async Task<ConnectedTable> GetTableAsync(Guid publicId, CancellationToken ct) =>
        await connectedTables.GetByPublicIdAsync(publicId, ct) ?? throw new NotFoundException("ConnectedTable", publicId);

    public static ConnectedRunResult ToResult(ConnectedRun r, IReadOnlyDictionary<long, string> names) => new(
        r.PublicId, r.TriggeredBy, names.GetValueOrDefault(r.TriggeredByUserId), r.Status, r.IsIncremental, r.RowsRead, r.Inserted, r.Updated,
        r.Unchanged, r.Reverted, r.Deleted, r.Skipped, r.Errored, r.HeldDeleteCount, r.ErrorDetail, ConnectedTime.Utc(r.QueuedOn), ConnectedTime.Utc(r.StartedOn), ConnectedTime.Utc(r.CompletedOn));
}
