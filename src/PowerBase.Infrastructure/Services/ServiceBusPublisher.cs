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
    // False when AzureServiceBus:ConnectionString is missing or still the placeholder: publishing is then skipped (one warning)
    // instead of every call dialling a host that cannot exist and retrying for seconds in the background.
    private readonly bool _configured;

    public ServiceBusPublisher(IConfiguration configuration, Microsoft.Extensions.Logging.ILogger<ServiceBusPublisher>? logger = null)
    {
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
        
        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(queueName);
    }

    public async Task PublishAsync<T>(T message, CancellationToken ct = default)
    {
        if (!_configured) return;
        var json = JsonSerializer.Serialize(message);
        var busMessage = new ServiceBusMessage(Encoding.UTF8.GetBytes(json));
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
                var serviceBusMessage = new ServiceBusMessage(Encoding.UTF8.GetBytes(json));

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
}
