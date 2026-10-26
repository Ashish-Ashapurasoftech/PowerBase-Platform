using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public class ServiceBusPublisher : IMessagePublisher
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly ServiceBusSender _prioritySender;
    private readonly Microsoft.Extensions.Logging.ILogger<ServiceBusPublisher>? _logger;
    // False when AzureServiceBus:ConnectionString is missing or still the placeholder: publishing is then skipped (one warning)
    // instead of every call dialling a host that cannot exist and retrying for seconds in the background.
    private readonly bool _configured;

    public ServiceBusPublisher(IConfiguration configuration, Microsoft.Extensions.Logging.ILogger<ServiceBusPublisher>? logger = null)
    {
        _logger = logger;
        var connectionString = configuration["AzureServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith("<") || !connectionString.Contains("Endpoint="))
        {
            connectionString = "Endpoint=sb://mock.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=mock";
            logger?.LogWarning("AzureServiceBus:ConnectionString is not configured; search-index updates will not be queued until it is set.");
        }
        else
        {
            _configured = true;
        }
        var queueName = configuration["AzureServiceBus:SearchIndexQueue"] ?? "search-indexing-queue";
        var priorityQueueName = configuration["AzureServiceBus:SearchIndexPriorityQueue"] ?? "search-indexing-priority-queue";
        
        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(queueName);
        _prioritySender = _client.CreateSender(priorityQueueName);
    }

    public async Task PublishPriorityAsync<T>(T message, CancellationToken ct = default)
    {
        if (!_configured) return;
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 256 * 1024)
        {
            throw new InvalidOperationException($"Message size {bytes.Length} bytes exceeds the 256 KB Service Bus limit.");
        }

        var busMessage = CreateServiceBusMessage(message, bytes);
        try
        {
            await _prioritySender.SendMessageAsync(busMessage, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to publish to priority queue {PriorityQueue}. Falling back to standard queue {StandardQueue}.", 
                _prioritySender.EntityPath, _sender.EntityPath);
            await _sender.SendMessageAsync(busMessage, ct);
        }
    }

    public async Task PublishAsync<T>(T message, CancellationToken ct = default)
    {
        if (!_configured) return;
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 256 * 1024)
        {
            throw new InvalidOperationException($"Message size {bytes.Length} bytes exceeds the 256 KB Service Bus limit.");
        }

        var busMessage = CreateServiceBusMessage(message, bytes);
        await _sender.SendMessageAsync(busMessage, ct);
    }

    public async Task PublishBatchAsync<T>(IEnumerable<T> messages, CancellationToken ct = default)
    {
        if (!_configured) return;
        var messageBatch = await _sender.CreateMessageBatchAsync(ct);
        try
        {
            foreach (var msg in messages)
            {
                var json = JsonSerializer.Serialize(msg);
                var bytes = Encoding.UTF8.GetBytes(json);
                if (bytes.Length > 256 * 1024)
                {
                    continue; // Skip oversized single message in bulk publish to avoid failing the entire batch
                }

                var serviceBusMessage = CreateServiceBusMessage(msg, bytes);

                if (!messageBatch.TryAddMessage(serviceBusMessage))
                {
                    // Batch is full, send current batch
                    await _sender.SendMessagesAsync(messageBatch, ct);
                    
                    // Dispose old and create a new batch
                    messageBatch.Dispose();
                    messageBatch = await _sender.CreateMessageBatchAsync(ct);
                    
                    if (!messageBatch.TryAddMessage(serviceBusMessage))
                    {
                        throw new System.Exception("Message is too large to fit in an empty batch.");
                    }
                }
            }
            if (messageBatch.Count > 0)
            {
                await _sender.SendMessagesAsync(messageBatch, ct);
            }
        }
        finally
        {
            messageBatch.Dispose();
        }
    }

    private static ServiceBusMessage CreateServiceBusMessage<T>(T message, byte[] bodyBytes)
    {
        var msg = new ServiceBusMessage(bodyBytes);
        if (message is PowerBase.Application.Common.Models.SearchIndexMessage sim && sim.RecordPublicId != Guid.Empty)
        {
            msg.MessageId = $"{sim.TenantId}_{sim.TableId}_{sim.RecordPublicId}_{sim.Action}";
            msg.CorrelationId = sim.RecordPublicId.ToString();
        }
        else
        {
            msg.MessageId = Guid.NewGuid().ToString();
            msg.CorrelationId = Guid.NewGuid().ToString();
        }
        return msg;
    }
}
