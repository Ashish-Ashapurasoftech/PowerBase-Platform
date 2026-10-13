using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineTriggerInterceptor
{
    // changedFieldIds contains table FIDs for single-record writes.
    Task InterceptAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string triggerEvent,
        CancellationToken ct = default,
        IReadOnlyDictionary<long, object?>? beforeValues = null,
        IReadOnlyList<long>? changedFieldIds = null);

    /// <summary>Whether any active pipeline listens to record events on <paramref name="table"/>. A writer that creates many records
    /// asks once per batch and skips <see cref="InterceptAsync"/> for the whole batch when nobody listens (the answer is the same
    /// for every record: the call would only look the listeners up again and find none). True when it cannot tell.</summary>
    Task<bool> HasActiveSubscriptionsAsync(AppTable table, CancellationToken ct = default);

    /// <summary>"Record added" events for many just-created records, written together. Same events as calling
    /// <see cref="InterceptAsync"/> once per record, with one subscription lookup and one outbox insert for the lot — unless a
    /// listener treats a batch differently from single records (a bulk-event trigger, or a record-count limit), in which case
    /// each record is intercepted on its own exactly as before.</summary>
    Task InterceptAddedManyAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<(Guid RecordPublicId, IReadOnlyDictionary<long, object?> Values)> records,
        CancellationToken ct = default);

    // PipelineRecordChange.ChangedFieldIds contains internal AppField IDs.
    Task InterceptBulkAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<PowerBase.Application.Common.Models.PipelineRecordChange> recordChanges,
        Guid batchId,
        Guid correlationId,
        long? triggeredBy,
        CancellationToken ct = default);
}
