using System;
using System.Data;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using Dapper;

namespace PowerBase.Infrastructure.Pipelines;

public class DatabasePipelineExecutionQueue : IPipelineExecutionQueue
{
    private readonly IMainPipelineQueueRepository _queueRepo;
    private readonly IControlConnectionFactory _controlConnFactory;
    private readonly ITenantConnectionResolver _tenantResolver;
    private readonly ILogger<DatabasePipelineExecutionQueue> _logger;

    public DatabasePipelineExecutionQueue(
        IMainPipelineQueueRepository queueRepo,
        IControlConnectionFactory controlConnFactory,
        ITenantConnectionResolver tenantResolver,
        ILogger<DatabasePipelineExecutionQueue> logger)
    {
        _queueRepo = queueRepo;
        _controlConnFactory = controlConnFactory;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    public void QueueTask(PipelineExecutionTask task)
    {
        QueueTaskAsync(task).GetAwaiter().GetResult();
    }

    private async Task QueueTaskAsync(PipelineExecutionTask task)
    {
        if (task == null) return;

        try
        {
            // 1. Resolve TenantPublicId from Control DB
            Guid tenantPublicId;
            using (var controlConn = _controlConnFactory.Create())
            {
                await controlConn.OpenAsync();
                tenantPublicId = await controlConn.QuerySingleAsync<Guid>(
                    "SELECT PublicId FROM meta.Tenant WHERE Id = @TenantId", new { task.TenantId });
            }

            // 2. Resolve PipelinePublicId from Tenant DB
            Guid pipelinePublicId;
            var tenantConnStr = await _tenantResolver.ResolveAsync(task.TenantId);
            using (var tenantConn = new Microsoft.Data.SqlClient.SqlConnection(tenantConnStr))
            {
                await tenantConn.OpenAsync();
                pipelinePublicId = await tenantConn.QuerySingleAsync<Guid>(
                    "SELECT PublicId FROM meta.Pipeline WHERE Id = @PipelineId", new { task.PipelineId });
            }

            // 3. Determine QueueSource and TriggerStep information if available
            string queueSource = "Manual";
            if (task.TriggerEvent == "webhook")
            {
                queueSource = "Webhook";
            }
            else if (task.TriggerEvent == "schedule" || task.TriggerEvent == "pipeline_schedule")
            {
                queueSource = "Schedule";
            }
            else if (task.TriggerEvent == "new-event")
            {
                queueSource = "Event";
            }

            // For Manual/Schedule jobs, check if we have a trigger step. If not, they remain null.
            long? triggerStepId = null;
            string? triggerStepRefId = null;
            
            if (queueSource == "Webhook" || queueSource == "Schedule" || queueSource == "Event")
            {
                try
                {
                    using var doc = JsonDocument.Parse(task.TriggerPayloadJson);
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
                    // Ignore parsing errors, columns remain null
                }
            }

            var messageId = Guid.TryParse(task.MessageId, out var parsedMsgId) ? parsedMsgId : Guid.NewGuid();
            var correlationId = Guid.TryParse(task.CorrelationId, out var parsedCorrId) ? parsedCorrId : Guid.NewGuid();

            var job = new PipelineQueue
            {
                MessageId = messageId,
                TenantId = task.TenantId,
                TenantPublicId = tenantPublicId,
                PipelineId = task.PipelineId,
                PipelinePublicId = pipelinePublicId,
                QueueSource = queueSource,
                TriggerStepId = triggerStepId,
                TriggerStepRefId = triggerStepRefId,
                TriggerEvent = task.TriggerEvent,
                TriggerPayloadJson = task.TriggerPayloadJson,
                PayloadHash = PayloadHashHelper.ComputeHash(task.TriggerPayloadJson),
                TriggeredBy = task.TriggeredBy == 0 ? null : task.TriggeredBy,
                TriggerTablePublicId = task.TriggerTablePublicId,
                CorrelationId = correlationId,
                Depth = task.Depth,
                PipelineChain = task.CorrelationId ?? "[]",
                BatchId = null,
                VariablesJson = task.VariablesJson,
                PayloadVersion = "1.0",
                EventTimestamp = DateTime.UtcNow,
                Status = "Pending",
                AttemptCount = 0,
                MaxAttempts = 5
            };

            try
            {
                var id = await _queueRepo.EnqueueAsync(job);
                _logger.LogInformation("Successfully enqueued pipeline queue job {Id} with MessageId {MessageId} from source {Source}.", id, messageId, queueSource);
                DatabasePipelineQueueWakeNotifier.Wake();
            }
            catch (DuplicateMessageException)
            {
                var existing = await _queueRepo.GetByMessageIdAsync(messageId);
                if (existing != null)
                {
                    bool matches = existing.TenantId == job.TenantId &&
                                   existing.PipelinePublicId == job.PipelinePublicId &&
                                   System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(existing.PayloadHash, job.PayloadHash);

                    if (matches)
                    {
                        throw new MessageDeduplicatedException(messageId);
                    }
                    else
                    {
                        throw new MessageCollisionException(messageId);
                    }
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            if (ex is MessageDeduplicatedException || ex is MessageCollisionException)
            {
                throw;
            }
            _logger.LogError(ex, "Failed to enqueue pipeline task from source.");
            throw;
        }
    }
}
