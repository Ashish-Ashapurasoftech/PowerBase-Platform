using System;
using PowerBase.Domain.Enums;

namespace PowerBase.Infrastructure.Pipelines;

public static class PipelineEventMapper
{
    public static PipelineRecordEventType? Map(string triggerEvent)
    {
        return (triggerEvent ?? "").ToLower().Trim() switch
        {
            "record-added" => PipelineRecordEventType.Added,
            "record-updated" => PipelineRecordEventType.Modified,
            "record-deleted" => PipelineRecordEventType.Deleted,
            _ => null
        };
    }

    public static string MapToString(PipelineRecordEventType eventType)
    {
        return eventType switch
        {
            PipelineRecordEventType.Added => "Added",
            PipelineRecordEventType.Modified => "Modified",
            PipelineRecordEventType.Deleted => "Deleted",
            _ => throw new ArgumentOutOfRangeException(nameof(eventType))
        };
    }

    public static bool IsEventEnabled(PipelineRecordEventType eventType, bool onAdded, bool onModified, bool onDeleted)
    {
        return eventType switch
        {
            PipelineRecordEventType.Added => onAdded,
            PipelineRecordEventType.Modified => onModified,
            PipelineRecordEventType.Deleted => onDeleted,
            _ => false
        };
    }
}
