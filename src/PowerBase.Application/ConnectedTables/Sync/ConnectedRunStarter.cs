using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;

namespace PowerBase.Application.ConnectedTables.Sync;

/// <summary>The settings a run uses, frozen when it is queued so later edits never change a run in flight.</summary>
public sealed record ConnectedRunSnapshot(
    bool FilterEnabled, string? FilterJson, string UpdateMode, int RefreshKeySourceFid, int MappingVersion, int FullRefreshVersion, byte MassDeleteGuardPct)
{
    public static ConnectedRunSnapshot Of(ConnectedTable t) =>
        new(t.FilterEnabled, t.FilterJson, t.UpdateMode, t.RefreshKeySourceFid, t.MappingVersion, t.FullRefreshVersion, t.MassDeleteGuardPct);
}

/// <summary>Queues a refresh: the run row in the tenant DB, then the dispatch item in the control DB. Manual refreshes,
/// the scheduler and the API all start runs here, so every run is created the same way.</summary>
public sealed class ConnectedRunStarter(IConnectedRunRepository runs, IConnectedRunQueue queue, IQueryContext context)
{
    public async Task<ConnectedRun> QueueAsync(ConnectedTable table, string trigger, long userId, bool confirmMassDelete, CancellationToken ct)
    {
        if (table.Status == ConnectedTableStatuses.Removed || table.IsDeleted)
            throw ConnectedTableException.Conflict(ConnectedErrorCodes.SourceUnavailable, "This connected table no longer exists.");

        var run = new ConnectedRun
        {
            ConnectedTableId = table.Id,
            TriggeredBy = trigger,
            TriggeredByUserId = userId,
            ConfirmMassDelete = confirmMassDelete,
            SnapshotJson = JsonSerializer.Serialize(ConnectedRunSnapshot.Of(table)),
        };
        (run.Id, run.PublicId) = await runs.CreateAsync(run, ct);

        try
        {
            await queue.EnqueueAsync(context.TenantId, run.PublicId, table.SourceTenantId, ct);
        }
        catch
        {
            await runs.CompleteAsync(run, ConnectedRunStatuses.Failed, "The refresh could not be queued. Try again.", CancellationToken.None);
            throw;
        }
        return run;
    }
}
