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
            maxWaitTime: TimeSpan.FromMilliseconds(500))
    {
    }
}
