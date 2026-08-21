using System;
using System.Collections.Generic;
using PowerBase.Domain.Enums;

namespace PowerBase.Application.Common.Models;

public record PipelineRecordChange(
    Guid RecordPublicId,
    IReadOnlyDictionary<long, object?> BeforeValues,
    IReadOnlyDictionary<long, object?> AfterValues,
    IReadOnlyList<long> ChangedFieldIds,
    PipelineRecordEventType EventType
);

public record PipelineBatchChange(
    Guid BatchId,
    PipelineRecordEventType EventType,
    IReadOnlyList<PipelineRecordChange> Records,
    Guid CorrelationId,
    long? TriggeredBy
)
{
    public int TotalRecordCount => Records.Count;
}
