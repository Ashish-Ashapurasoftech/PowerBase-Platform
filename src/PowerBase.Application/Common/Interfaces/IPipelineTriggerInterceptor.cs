using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IPipelineTriggerInterceptor
{
    Task InterceptAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid recordPublicId,
        IReadOnlyDictionary<long, object?> fieldValues,
        string triggerEvent,
        CancellationToken ct = default,
        IReadOnlyDictionary<long, object?>? beforeValues = null,
        IReadOnlyList<long>? changedFieldIds = null);

    Task InterceptBulkAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<PowerBase.Application.Common.Models.PipelineRecordChange> recordChanges,
        Guid batchId,
        Guid correlationId,
        long? triggeredBy,
        CancellationToken ct = default);
}
