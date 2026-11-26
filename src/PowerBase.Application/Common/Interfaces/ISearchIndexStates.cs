using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>A stored field that has just become Indexed: its AppField id and the MaterializeVersion that is in the index.</summary>
public sealed record SearchIndexedField(long FieldId, long TableId, int Fid, int Version);

/// <summary>
/// meta.AppField.SearchIndexState of stored Lookup / Summary / Formula fields (migrations 101 and 103): moved by the search indexer when a
/// field job carrying a field's values has written its last page. Only a field that is Indexing (sent at Ready) moves, and only for the job
/// that was created after the field was sent (SearchIndexRequestedOn), so a job of an older build never marks a newer one.
/// Normal fields are never Indexing, so these calls do nothing for them.
/// </summary>
public interface ISearchIndexStates
{
    /// <summary>
    /// The last page of field job <paramref name="jobId"/> (still at <paramref name="fromAfterId"/>) wrote field <paramref name="fid"/> of
    /// table <paramref name="tableId"/> into the index: the field becomes Indexed, with SearchExact = 0 until <see cref="CheckExactAsync"/>
    /// has looked at its values. Null when no stored field was waiting for this job.
    /// </summary>
    Task<SearchIndexedField?> MarkIndexedAsync(long tableId, long fid, long jobId, long fromAfterId, CancellationToken ct = default);

    /// <summary>
    /// Decision Q2: sets SearchExact = 1 when every stored value comes back unchanged through a double (what the index keeps, about 15
    /// significant digits), so sort and the shown value may come from the index too. Only while the field is still Indexed for that version.
    /// Reads one column of the whole table, so it runs outside the indexer's batch. Returns the value it stored (false on any doubt).
    /// </summary>
    Task<bool> CheckExactAsync(SearchIndexedField field, CancellationToken ct = default);

    /// <summary>The field job ended without writing the field (it has no column in the index, e.g. its pool is full): Failed.</summary>
    Task<bool> MarkNotIndexedAsync(long tableId, long fid, long jobId, CancellationToken ct = default);
}
