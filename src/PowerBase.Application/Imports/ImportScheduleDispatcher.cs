using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports;

public enum ImportDispatchOutcome
{
    /// <summary>Another scheduler already took this occurrence, or the schedule was changed meanwhile.</summary>
    NotClaimed,
    /// <summary>The schedule was removed or paused after it fell due; nothing runs and nothing is scheduled.</summary>
    Cleared,
    /// <summary>The previous run is still going, so this occurrence is left out and the next one is kept.</summary>
    SkippedOverlap,
    /// <summary>A mapped field changed since the import was saved; recorded as a failed run so the history says why.</summary>
    SkippedNeedsAttention,
    /// <summary>Ready to be started by the person it runs as.</summary>
    Claimed
}

/// <summary>The scheduler's decisions, kept apart from the background worker so they can be tested: which due definition to
/// start, when it is next due, and what to leave out. The worker owns scopes and identities; the starting itself is
/// <see cref="StartImportRunHandler"/>, so a scheduled run is validated exactly like a manual one.</summary>
public sealed class ImportScheduleDispatcher(
    IImportDefinitionRepository definitions, IImportRunRepository runs, IAppTableRepository tables, IImportDefinitionChecker checker)
{
    public const int BatchSize = 50;

    public Task<IReadOnlyList<ImportDefinition>> ListDueAsync(DateTime nowUtc, CancellationToken ct) =>
        definitions.ListDueAsync(nowUtc, BatchSize, ct);

    /// <summary>Takes one due occurrence. The schedule moves to its next time first, counted from now, so a server that was down
    /// runs the import once on return (never once per missed time) and a crash after this point cannot run it twice.</summary>
    public async Task<ImportDispatchOutcome> ClaimAsync(ImportDefinition def, DateTime nowUtc, CancellationToken ct)
    {
        var due = def.NextRunOn!.Value;
        var schedule = ImportJson.Deserialize<ImportSchedule>(def.ScheduleJson);
        if (schedule is not { Enabled: true })
            return await definitions.TryAdvanceScheduleAsync(def.Id, due, null, ct) ? ImportDispatchOutcome.Cleared : ImportDispatchOutcome.NotClaimed;

        DateTime? next;
        try
        {
            var computed = ImportScheduling.NextRun(schedule, nowUtc);
            next = computed == DateTime.MaxValue ? null : computed;
        }
        catch (Exception) { next = null; } // a stored schedule that no longer computes must not be retried every minute
        if (!await definitions.TryAdvanceScheduleAsync(def.Id, due, next, ct)) return ImportDispatchOutcome.NotClaimed;

        // A flagged import is looked at again: if what was wrong has been fixed it runs now, not only after someone opens it.
        if (def.NeedsAttention) await checker.RefreshAsync(def, ct);
        if (def.NeedsAttention)
        {
            await RecordFailedRunAsync(def, $"Skipped: this import needs attention before it can run: {def.AttentionReason ?? "a mapped field changed"}.", ct);
            return ImportDispatchOutcome.SkippedNeedsAttention;
        }
        return await runs.HasActiveRunAsync(def.Id, ct) ? ImportDispatchOutcome.SkippedOverlap : ImportDispatchOutcome.Claimed;
    }

    /// <summary>Puts a failed scheduled run in the history, so a person looking at the import sees why nothing ran, and the
    /// person it runs as is told through the same in-app notice as any finished run.</summary>
    public async Task RecordFailedRunAsync(ImportDefinition def, string reason, CancellationToken ct)
    {
        var destination = await tables.GetByIdAsync(def.DestinationTableId, ct);
        var source = def.SourceTableId is { } sourceId ? await tables.GetByIdAsync(sourceId, ct) : null;
        var snapshot = new ImportRunSnapshot(destination.PublicId, source is null ? new ImportDefinitionConfig { Name = def.Name } : ImportConfigMapper.ToConfig(def, source.PublicId), null);
        var run = new ImportRun
        {
            ImportDefinitionId = def.Id, TriggeredBy = ImportTrigger.Schedule, TriggeredByUserId = def.RunAsUserId,
            Status = ImportRunStatus.Queued, DefinitionSnapshotJson = ImportJson.Serialize(snapshot)
        };
        await runs.CreateAsync(run, ct);
        await runs.CompleteAsync(run.Id, ImportRunStatus.Failed, reason.Length > 900 ? reason[..900] : reason, ct: ct);
    }
}
