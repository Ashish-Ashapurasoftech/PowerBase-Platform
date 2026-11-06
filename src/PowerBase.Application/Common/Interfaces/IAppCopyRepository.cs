using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppCopyRepository
{
    /// <summary>Copies an app's structure (no records) with meta.usp_CopyAppSchema, in one transaction together with the
    /// new app's data key when the app is encrypted. Throws <c>DuplicateException</c> when the name is taken.</summary>
    Task<AppSchemaCopyResult> CopySchemaAsync(AppSchemaCopyRequest request, CancellationToken ct = default);

    /// <summary>Copy App – Schema + Data (migration 091). The structure is copied as by <see cref="CopySchemaAsync"/>,
    /// into a Draft app that keeps the original's data key; that step stops when <paramref name="ct"/> is cancelled. The
    /// records are then copied in chunks whatever happens to the request (a closed tab, a response cut by the hosting
    /// timeout), with their progress kept in meta.AppCopyRun, and the app becomes Active. A failure removes the copy and
    /// is thrown. Throws <c>DuplicateException</c> when the name is taken.</summary>
    Task<AppDataCopyResult> CopyWithDataAsync(AppDataCopyRequest request, CancellationToken ct = default);

    /// <summary>Removes copies with data that a stopped or restarted server left behind: a running copy's API process moves
    /// its run on every few seconds, so a run that stopped moving for a couple of minutes has no process any more. They
    /// then show as failed.</summary>
    Task DiscardStaleDataCopiesAsync(CancellationToken ct = default);

    /// <summary>The progress of a copy with data started by <paramref name="userId"/> under this id, read from the database
    /// so any API instance can answer; null when there is none (or its first step has not committed yet).</summary>
    Task<AppCopyProgress?> GetDataCopyProgressAsync(Guid progressId, long userId, CancellationToken ct = default);

    /// <summary>The copies with data <paramref name="userId"/> started that the app list shows: running ones, and finished
    /// or failed ones of the last few days that were not dismissed. Newest first.</summary>
    Task<IReadOnlyList<AppCopyRunSummary>> ListDataCopiesAsync(long userId, CancellationToken ct = default);

    /// <summary>Hides a finished or failed copy of <paramref name="userId"/> from the app list. A running copy stays.</summary>
    Task DismissDataCopyAsync(Guid copyAppPublicId, long userId, CancellationToken ct = default);
}
