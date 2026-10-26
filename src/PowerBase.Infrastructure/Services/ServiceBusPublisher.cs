using System;
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
    private readonly bool _priorityEnabled;

    public ServiceBusPublisher(IConfiguration configuration, Microsoft.Extensions.Logging.ILogger<ServiceBusPublisher>? logger = null)
    {
        _logger = logger;
        _client = ServiceBusRetry.CreateClient(configuration, out var configured, logger);
        _configured = configured;
        var queueName = configuration["AzureServiceBus:SearchIndexQueue"] ?? "search-indexing-queue";
        var priorityQueueName = configuration["AzureServiceBus:SearchIndexPriorityQueue"] ?? "search-indexing-priority-queue";
        _priorityEnabled = !string.Equals(configuration["AzureServiceBus:SearchIndexPriorityQueueEnabled"], "false", StringComparison.OrdinalIgnoreCase);

        _sender = _client.CreateSender(queueName);
        _prioritySender = _client.CreateSender(priorityQueueName);
    }

    public async Task PublishPriorityAsync<T>(T message, CancellationToken ct = default)
    {
        if (!_configured) return;
        if (!_priorityEnabled)
        {
            await PublishAsync(message, ct); // no priority queue in this deployment
            return;
        }
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (IsOversized(bytes))
        {
            var tooBig = new InvalidOperationException($"Message size {bytes.Length} bytes exceeds the 256 KB Service Bus limit.");
            LogSendFailure(tooBig, message, _prioritySender.EntityPath);
            throw tooBig;
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
            try
            {
                await _sender.SendMessageAsync(CreateServiceBusMessage(message, bytes), ct);
            }
            catch (Exception fallbackEx)
            {
                LogSendFailure(fallbackEx, message, _sender.EntityPath);
                throw;
            }
        }
    }

    public async Task PublishAsync<T>(T message, CancellationToken ct = default)
    {
        if (!_configured) return;
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (IsOversized(bytes))
        {
            var tooBig = new InvalidOperationException($"Message size {bytes.Length} bytes exceeds the 256 KB Service Bus limit.");
            LogSendFailure(tooBig, message, _sender.EntityPath);
            throw tooBig;
        }

        var busMessage = CreateServiceBusMessage(message, bytes);
        try
        {
            await _sender.SendMessageAsync(busMessage, ct);
        }
        catch (Exception ex)
        {
            // Most callers do not await this (fire-and-forget), so without this line a failed send left no trace at all.
            LogSendFailure(ex, message, _sender.EntityPath);
            throw;
        }
    }

    public async Task PublishBatchAsync<T>(IEnumerable<T> messages, CancellationToken ct = default)
    {
        if (!_configured) return;
        var messageBatch = await _sender.CreateMessageBatchAsync(ct);
        var sent = 0;
        try
        {
            foreach (var msg in messages)
            {
                var json = JsonSerializer.Serialize(msg);
                var bytes = Encoding.UTF8.GetBytes(json);
                if (IsOversized(bytes))
                {
                    // Skipped so one huge record does not fail the whole batch, but never silently: this record will not be
                    // indexed from this message, and the log names it so it can be reindexed.
                    _logger?.LogError("Search index message skipped, {Size} bytes exceeds the 256 KB Service Bus limit: {Message}", bytes.Length, Describe(msg));
                    continue;
                }

                var serviceBusMessage = CreateServiceBusMessage(msg, bytes);

                if (!messageBatch.TryAddMessage(serviceBusMessage))
                {
                    // Batch is full, send current batch
                    sent += messageBatch.Count;
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to publish a batch of search index messages to {Queue}; {Sent} were sent before the failure. Records in the unsent part will not be indexed until reindexed.", _sender.EntityPath, sent);
            throw;
        }
        finally
        {
            messageBatch.Dispose();
        }
    }

    internal const int MaxMessageBytes = 256 * 1024;

    internal static bool IsOversized(byte[] body) => body.Length > MaxMessageBytes;

    /// <summary>What a message is about, for logs: ids only, never field values.</summary>
    internal static string Describe<T>(T message) =>
        message is PowerBase.Application.Common.Models.SearchIndexMessage sim
            ? $"{sim.Action} record {sim.RecordPublicId} (tenant {sim.TenantId}, table {sim.TableId})"
            : typeof(T).Name;

    private void LogSendFailure<T>(Exception ex, T message, string queue) =>
        _logger?.LogError(ex, "Failed to publish search index message to {Queue}: {Message}. The index will not reflect it until the record is reindexed.", queue, Describe(message));

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
