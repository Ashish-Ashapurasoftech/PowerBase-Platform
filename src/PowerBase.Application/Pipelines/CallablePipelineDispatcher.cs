using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>Dispatches calls through the durable queue in the pipeline owner's tenant.</summary>
public sealed class CallablePipelineDispatcher(IPipelineRepository repository, IPipelineExecutionQueue queue)
{
    public async Task<IReadOnlyList<Guid>> DispatchAsync(long tenantId, long ownerId, long callerPipelineId,
        Guid parentMessageId, Guid callerStepId, string executionPath, string? correlationId, int depth,
        CallablePipelineDefinition definition, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct, string? pipelineChain = null)
    {
        if (tenantId <= 0 || ownerId <= 0 || parentMessageId == Guid.Empty)
            throw new PipelineNonRetryableException("Callable dispatch requires a tenant, pipeline owner and stable run message identity.");
        var targets = await repository.FindCallablePipelinesAsync(ownerId, definition.Definition, ct);
        if (targets.Count == 0) throw new PipelineNonRetryableException("No active pipeline owned by you matches this Call Definition.");
        var messages = new List<Guid>();
        // Older manual queue jobs stored a correlation GUID in PipelineChain instead of a JSON array.
        var chain = string.IsNullOrWhiteSpace(pipelineChain) || Guid.TryParse(pipelineChain, out _)
            ? new List<long>() : JsonSerializer.Deserialize<List<long>>(pipelineChain) ?? new();
        if (chain.Count == 0 || chain[^1] != callerPipelineId) chain.Add(callerPipelineId);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (target.CreatedBy != ownerId || !target.IsActive || target.IsDeleted)
                throw new PipelineNonRetryableException("The called pipeline is unavailable.");
            var steps = await repository.GetStepsByPipelineIdAsync(target.Id, ct);
            var trigger = steps.Where(step => !step.IsDeleted && step.ParentStepId == null).OrderBy(step => step.DisplayOrder).ThenBy(step => step.Id).FirstOrDefault();
            if (trigger is null || !trigger.IsValidated || trigger.Type != "trigger" || trigger.Subtype != "pipeline-called" ||
                CallablePipelineDefinition.ValidateConfig(trigger.ConfigJson, false).Definition != definition.Definition)
                throw new PipelineNonRetryableException("The called pipeline's definition changed. Retry with the matching definition.");

            var messageId = CreateMessageId(parentMessageId, callerStepId, executionPath, target.PublicId);
            queue.QueueTask(new PipelineExecutionTask
            {
                TenantId = tenantId, PipelineId = target.Id, TriggeredBy = ownerId,
                TriggerEvent = "pipeline-called", MessageId = messageId.ToString(),
                CorrelationId = Guid.TryParse(correlationId, out var correlation) ? correlation.ToString() : parentMessageId.ToString(), Depth = checked(depth + 1),
                PipelineChain = JsonSerializer.Serialize(chain.Append(target.Id)),
                TriggerPayloadJson = JsonSerializer.Serialize(new
                {
                    TriggerStepId = trigger.Id, TriggerStepRefId = trigger.RefId,
                    CallDefinition = definition.Definition, Arguments = arguments,
                    CallerPipelineId = callerPipelineId, CallerStepId = callerStepId,
                    ParentMessageId = parentMessageId
                })
            });
            messages.Add(messageId);
        }
        return messages;
    }

    public static Guid CreateMessageId(Guid parentMessageId, Guid stepId, string executionPath, Guid targetId)
    {
        var identity = JsonSerializer.Serialize(new { parentMessageId, stepId, executionPath, targetId });
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }
}
