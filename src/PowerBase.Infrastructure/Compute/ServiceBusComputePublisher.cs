using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Services;

namespace PowerBase.Infrastructure.Compute;

/// <summary>
/// Publishes "table X of tenant Y has work" to the compute queue. The queue has sessions enabled and SessionId = "{tenantId}:{tableId}",
/// so one worker owns one table at a time. Separate from <see cref="ServiceBusPublisher"/> (search indexing) on purpose: that one has
/// no session or queue parameter and its messages mean something else. Nothing is sent while Compute:Enabled is off or Service Bus is
/// not configured. A lost message only delays the value: the outbox row is still there and the relay re-publishes it.
/// </summary>
public sealed class ServiceBusComputePublisher : IComputeQueuePublisher
{
    private readonly ServiceBusSender _sender;
    private readonly bool _configured;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServiceBusComputePublisher>? _logger;

    public ServiceBusComputePublisher(IConfiguration configuration, ILogger<ServiceBusComputePublisher>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
        var client = ServiceBusRetry.CreateClient(configuration, out var configured, logger);
        _configured = configured;
        _sender = client.CreateSender(ComputeConfig.ReadCompute(configuration).QueueName);
    }

    private bool Active => _configured && ComputeConfig.ReadCompute(_configuration).Enabled;

    public async Task PublishAsync(ComputeQueueMessage message, CancellationToken ct = default)
    {
        if (!Active) return;
        try
        {
            await _sender.SendMessageAsync(ToServiceBusMessage(message), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to publish a compute message for tenant {TenantId}, table {TableId}. The outbox relay will re-publish it.", message.TenantId, message.TableId);
            throw;
        }
    }

    public async Task PublishBatchAsync(IEnumerable<ComputeQueueMessage> messages, CancellationToken ct = default)
    {
        if (!Active) return;
        var distinct = messages.DistinctBy(m => m.SessionId).ToList();
        if (distinct.Count == 0) return;

        var batch = await _sender.CreateMessageBatchAsync(ct);
        try
        {
            foreach (var message in distinct)
            {
                var busMessage = ToServiceBusMessage(message);
                if (batch.TryAddMessage(busMessage)) continue;

                await _sender.SendMessagesAsync(batch, ct);
                batch.Dispose();
                batch = await _sender.CreateMessageBatchAsync(ct);
                if (!batch.TryAddMessage(busMessage)) throw new InvalidOperationException("A compute message does not fit in an empty batch.");
            }
            if (batch.Count > 0) await _sender.SendMessagesAsync(batch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to publish {Count} compute message(s). The outbox relay will re-publish them.", distinct.Count);
            throw;
        }
        finally
        {
            batch.Dispose();
        }
    }

    /// <summary>The Service Bus message for a compute message. Internal so the unit tests can check the session id and body.</summary>
    internal static ServiceBusMessage ToServiceBusMessage(ComputeQueueMessage message) =>
        new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)))
        {
            SessionId = message.SessionId,
            MessageId = $"{message.SessionId}:{Guid.NewGuid():N}",
            ContentType = "application/json",
            Subject = "compute"
        };
}
