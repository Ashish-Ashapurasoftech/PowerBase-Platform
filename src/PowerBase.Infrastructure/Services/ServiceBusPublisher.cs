using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

public class ServiceBusPublisher : IMessagePublisher
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public ServiceBusPublisher(IConfiguration configuration)
    {
        var connectionString = configuration["AzureServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith("<") || !connectionString.Contains("Endpoint="))
        {
            connectionString = "Endpoint=sb://mock.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=mock";
        }
        var queueName = configuration["AzureServiceBus:SearchIndexQueue"] ?? "search-indexing-queue";
        
        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(queueName);
    }

    public async Task PublishAsync<T>(T message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message);
        var busMessage = new ServiceBusMessage(Encoding.UTF8.GetBytes(json));
        await _sender.SendMessageAsync(busMessage, ct);
    }

    public async Task PublishBatchAsync<T>(IEnumerable<T> messages, CancellationToken ct = default)
    {
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
