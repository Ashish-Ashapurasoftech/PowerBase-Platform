using System;
using System.Text.Json;
using PowerBase.Domain.Entities;

namespace PowerBase.Infrastructure.Pipelines;

public static class TenantPipelinePayloadMapper
{
    public static PipelineQueue MapFromOutbox(PipelineOutboxItem outbox, long tenantId, Guid tenantPublicId, Guid pipelinePublicId)
    {
        var hash = PayloadHashHelper.ComputeHash(outbox.TriggerPayloadJson);

        long? triggerStepId = null;
        string? triggerStepRefId = null;

        try
        {
            using var doc = JsonDocument.Parse(outbox.TriggerPayloadJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("TriggerStepId", out var stepIdProp) && stepIdProp.ValueKind == JsonValueKind.Number)
            {
                triggerStepId = stepIdProp.GetInt64();
            }
            if (root.TryGetProperty("TriggerStepRefId", out var refIdProp) && refIdProp.ValueKind == JsonValueKind.String)
            {
                triggerStepRefId = refIdProp.GetString();
            }
        }
        catch
        {
            // Fallback if payload JSON is malformed
        }

        return new PipelineQueue
        {
            MessageId = outbox.MessageId,
            TenantId = tenantId,
            TenantPublicId = tenantPublicId,
            PipelineId = outbox.PipelineId,
            PipelinePublicId = pipelinePublicId,
            QueueSource = "Event",
            TriggerStepId = triggerStepId,
            TriggerStepRefId = triggerStepRefId,
            TriggerEvent = outbox.TriggerEvent,
            TriggerPayloadJson = outbox.TriggerPayloadJson,
            PayloadHash = hash,
            TriggeredBy = outbox.TriggeredBy,
            TriggerTablePublicId = outbox.TriggerTablePublicId,
            CorrelationId = outbox.CorrelationId,
            Depth = outbox.Depth,
            PipelineChain = outbox.PipelineChain,
            BatchId = outbox.BatchId,
            VariablesJson = null,
            PayloadVersion = outbox.PayloadVersion,
            EventTimestamp = outbox.CreatedOn,
            Status = "Pending",
            AttemptCount = 0,
            MaxAttempts = 5,
            CreatedOn = DateTime.UtcNow,
            LastModifiedOn = DateTime.UtcNow
        };
    }
}
