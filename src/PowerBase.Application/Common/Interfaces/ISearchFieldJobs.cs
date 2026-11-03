using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>A field job still running (or stopped): which field, which column to empty, and how far it has got.</summary>
public sealed record SearchFieldJobEntry(long Id, long TableId, long AppId, IndexAction Action, long FieldId, string? Slot, string? EndSlot, long AfterId);

/// <summary>
/// meta.SearchFieldJob: field changes still on their way to every record in the search index. A job is recorded before its first message
/// is sent and removed after its last page is indexed, so a lost or dead-lettered page is resumed instead of leaving the field half done.
/// Moving a job forward is compare-and-set, so a resent job and its original never both carry on.
/// </summary>
public interface ISearchFieldJobs
{
    /// <summary>Records the job (Action, TableId, AppId, FieldId, Slot, EndSlot, AfterId of <paramref name="job"/>) and returns its id; null
    /// when the job table does not exist yet in this tenant's database (the job then runs without being recorded, as before).</summary>
    Task<long?> StartAsync(SearchIndexMessage job, CancellationToken ct = default);

    /// <summary>Moves the job from <paramref name="fromAfterId"/> to <paramref name="toAfterId"/>. False when it is no longer there or
    /// another run of it moved it already: that run carries on, this one stops.</summary>
    Task<bool> AdvanceAsync(long jobId, long fromAfterId, long toAfterId, CancellationToken ct = default);

    /// <summary>Removes the job after its last page, if it is still at <paramref name="fromAfterId"/>. False when another run got there first.</summary>
    Task<bool> CompleteAsync(long jobId, long fromAfterId, CancellationToken ct = default);

    /// <summary>Takes up to <paramref name="max"/> jobs whose progress has not moved for <paramref name="stalledAfter"/>, under the attempt
    /// limit, and counts the attempt (so they are not taken again at once).</summary>
    Task<IReadOnlyList<SearchFieldJobEntry>> ClaimStalledAsync(int max, System.TimeSpan stalledAfter, int maxAttempts, CancellationToken ct = default);

    /// <summary>The jobs of one table that have not finished.</summary>
    Task<IReadOnlyList<SearchFieldJobEntry>> ListForTableAsync(long tableId, CancellationToken ct = default);

    /// <summary>Gives jobs that ran out of attempts another round (the daily sweep). Returns how many.</summary>
    Task<int> ReleaseParkedAsync(int maxAttempts, CancellationToken ct = default);
}
