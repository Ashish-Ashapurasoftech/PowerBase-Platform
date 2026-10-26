using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PowerBase.API.Workers;

public class SearchIndexerPriorityWorker : SearchIndexerWorker
{
    public SearchIndexerPriorityWorker(
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILogger<SearchIndexerPriorityWorker> logger)
        : base(
            configuration,
            serviceProvider,
            logger,
            configuration["AzureServiceBus:SearchIndexPriorityQueue"] ?? "search-indexing-priority-queue",
            maxBatchSize: 50,
            maxWaitTime: TimeSpan.FromMilliseconds(500),
            enabled: IsEnabled(configuration))
    {
    }

    /// <summary>On unless AzureServiceBus:SearchIndexPriorityQueueEnabled is "false". Turn it off where the priority queue has not been created.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        !string.Equals(configuration["AzureServiceBus:SearchIndexPriorityQueueEnabled"], "false", StringComparison.OrdinalIgnoreCase);
}
