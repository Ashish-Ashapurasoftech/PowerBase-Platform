using System;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// One way of creating Service Bus clients for the whole application (the publisher and the indexer's receiver): one retry policy, one
/// choice of credential, and the delivery limit the indexer works to. Transient failures (server busy, timeouts, connection resets) are
/// retried by the SDK with growing, jittered pauses, up to <see cref="MaxAttempts"/> times, before the failure is logged and surfaced.
/// </summary>
public static class ServiceBusRetry
{
    /// <summary>Retries after the first try, and the number of deliveries after which the indexer dead-letters a message itself
    /// (with the error text). Matches the broker's default maximum delivery count of 10; a queue configured lower dead-letters first.</summary>
    public const int MaxAttempts = 10;

    public static ServiceBusClientOptions ClientOptions() => new()
    {
        RetryOptions = new ServiceBusRetryOptions
        {
            Mode = ServiceBusRetryMode.Exponential,
            MaxRetries = MaxAttempts,
            Delay = TimeSpan.FromMilliseconds(800),
            MaxDelay = TimeSpan.FromSeconds(15),
            TryTimeout = TimeSpan.FromSeconds(60)
        }
    };

    /// <summary>
    /// With AzureServiceBus:FullyQualifiedNamespace (for example "mybus.servicebus.windows.net") the client signs in with the app's managed
    /// identity (roles "Azure Service Bus Data Sender" / "Data Receiver"); no secret is stored. Otherwise AzureServiceBus:ConnectionString is used.
    /// <paramref name="configured"/> is false when neither is set: the client then points at a placeholder host and publishing is skipped.
    /// </summary>
    public static ServiceBusClient CreateClient(IConfiguration configuration, out bool configured, ILogger? logger = null)
    {
        var ns = configuration["AzureServiceBus:FullyQualifiedNamespace"];
        if (!string.IsNullOrWhiteSpace(ns) && !ns.StartsWith("<"))
        {
            configured = true;
            return new ServiceBusClient(ns, new DefaultAzureCredential(), ClientOptions());
        }

        var connectionString = configuration["AzureServiceBus:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith("<") || !connectionString.Contains("Endpoint="))
        {
            configured = false;
            logger?.LogWarning("Neither AzureServiceBus:FullyQualifiedNamespace nor AzureServiceBus:ConnectionString is configured; search-index updates will not be queued until one is set.");
            return new ServiceBusClient("Endpoint=sb://mock.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=mock", ClientOptions());
        }

        configured = true;
        return new ServiceBusClient(connectionString, ClientOptions());
    }
}
