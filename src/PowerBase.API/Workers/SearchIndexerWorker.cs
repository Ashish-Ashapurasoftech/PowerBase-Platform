using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.API.Workers;

public class SearchIndexerWorker : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusReceiver _receiver;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SearchIndexerWorker> _logger;
    private const int MaxBatchSize = 1000;

    public SearchIndexerWorker(IConfiguration configuration, IServiceProvider serviceProvider, ILogger<SearchIndexerWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        
        var connectionString = configuration["AzureServiceBus:ConnectionString"] ?? "Endpoint=sb://mock.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=mock";
        var queueName = configuration["AzureServiceBus:SearchIndexQueue"] ?? "search-indexing-queue";

        _client = new ServiceBusClient(connectionString);
        _receiver = _client.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            PrefetchCount = MaxBatchSize
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try 
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait for a batch of messages (up to 1000) with a maximum wait time of 5 seconds to form a batch
                var messages = await _receiver.ReceiveMessagesAsync(maxMessages: MaxBatchSize, maxWaitTime: TimeSpan.FromSeconds(5), cancellationToken: stoppingToken);
                
                if (messages == null || !messages.Any())
                {
                    continue; // No messages, wait again
                }

                await ProcessMessageBatchAsync(messages, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Worker is stopping
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Service Bus Receiver.");
        }
        finally
        {
            await _receiver.CloseAsync();
        }
    }

    private async Task ProcessMessageBatchAsync(IReadOnlyList<ServiceBusReceivedMessage> messages, CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var searchService = scope.ServiceProvider.GetRequiredService<IAzureSearchService>();
        var recordRepo = scope.ServiceProvider.GetRequiredService<IRecordRepository>();

        var documentsToIndex = new List<SearchIndexDocument>();
        var recordsToDelete = new List<(long TableId, Guid RecordPublicId)>();
        var messagesToComplete = new List<ServiceBusReceivedMessage>();

        foreach (var sbMessage in messages)
        {
            try
            {
                var message = JsonSerializer.Deserialize<SearchIndexMessage>(sbMessage.Body);
                if (message == null)
                {
                    messagesToComplete.Add(sbMessage); // Invalid message, discard
                    continue;
                }

                if (message.Action == IndexAction.Upsert)
                {
                    IReadOnlyDictionary<long, object?>? recordData = null;

                    if (message.Payload != null && message.Payload.Count > 0)
                    {
                        recordData = message.Payload.ToDictionary(
                            kvp => long.Parse(kvp.Key), 
                            kvp => 
                            {
                                if (kvp.Value is System.Text.Json.JsonElement el)
                                {
                                    return el.ValueKind switch
                                    {
                                        System.Text.Json.JsonValueKind.String => el.GetString(),
                                        System.Text.Json.JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.TryGetDouble(out var d) ? (object)d : el.GetRawText(),
                                        System.Text.Json.JsonValueKind.True => true,
                                        System.Text.Json.JsonValueKind.False => false,
                                        System.Text.Json.JsonValueKind.Null => null,
                                        _ => el.GetRawText()
                                    };
                                }
                                return kvp.Value;
                            }
                        );
                    }
                    else
                    {
                        recordData = await recordRepo.GetSearchableFieldsAsync(message.RecordPublicId, stoppingToken);
                    }

                    if (recordData != null && recordData.Count > 0)
                    {
                        documentsToIndex.Add(new SearchIndexDocument(
                            message.TenantId,
                            message.AppId,
                            message.TableId,
                            message.RecordPublicId,
                            recordData
                        ));
                    }
                }
                else if (message.Action == IndexAction.Delete)
                {
                    recordsToDelete.Add((message.TableId, message.RecordPublicId));
                }

                messagesToComplete.Add(sbMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing individual search index message in batch.");
                // We do not add it to messagesToComplete so it will be retried (abandoned)
            }
        }

        try
        {
            // Execute bulk actions for the current batch
            if (documentsToIndex.Any())
            {
                await searchService.BulkIndexRecordsAsync(documentsToIndex, stoppingToken);
            }

            if (recordsToDelete.Any())
            {
                // We group deletes by TableId since BulkDelete is table-scoped in theory, though we only have one index.
                var deleteGroups = recordsToDelete.GroupBy(x => x.TableId);
                foreach (var group in deleteGroups)
                {
                    await searchService.BulkDeleteRecordsAsync(group.Key, group.Select(x => x.RecordPublicId).ToList(), stoppingToken);
                }
            }

            // Complete successfully processed messages
            foreach (var sbMessage in messagesToComplete)
            {
                await _receiver.CompleteMessageAsync(sbMessage, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pushing bulk batch to Azure AI Search. Entire batch will be retried.");
            // Do not complete any messages if the batch failed to upload, they will be retried
        }
    }
}
