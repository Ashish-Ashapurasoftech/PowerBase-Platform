using System.Text.Json;
using Azure.Messaging.ServiceBus;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Services;

namespace PowerBase.ComputeWorker;

/// <summary>
/// Listens on the compute queue (sessions: one "{tenantId}:{tableId}" at a time per worker). For each message it drains that table's
/// ComputeOutbox rows through <see cref="ComputeTableProcessor"/>: the stale stored values are recomputed and the rows deleted. When
/// the database is busy the rows simply stay and the relay re-publishes them later.
/// </summary>
public sealed class ComputeQueueWorker : BackgroundService
{
    /// <summary>Sessions (= tables) handled at once by one replica. The per-database limit is the DatabaseGuard's job (Phase 3).</summary>
    internal const int MaxConcurrentSessions = 8;

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly IComputeSettings _settings;
    private readonly ComputeWorkerCounters _counters;
    private readonly ILogger<ComputeQueueWorker> _logger;

    public ComputeQueueWorker(IServiceProvider services, IConfiguration configuration, IComputeSettings settings,
        ComputeWorkerCounters counters, ILogger<ComputeQueueWorker> logger)
    {
        _services = services;
        _configuration = configuration;
        _settings = settings;
        _counters = counters;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = ComputeConfig.ReadCompute(_configuration);
        if (!options.Enabled)
        {
            _logger.LogInformation("Compute worker is switched off (Compute:Enabled=false); not listening on {Queue}.", options.QueueName);
            return;
        }

        await using var client = ServiceBusRetry.CreateClient(_configuration, out var configured, _logger);
        if (!configured)
        {
            _logger.LogWarning("Compute worker: Service Bus is not configured; not listening on {Queue}.", options.QueueName);
            return;
        }

        await using var processor = client.CreateSessionProcessor(options.QueueName, new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentSessions = MaxConcurrentSessions,
            MaxConcurrentCallsPerSession = 1,
            SessionIdleTimeout = TimeSpan.FromSeconds(5)
        });
        processor.ProcessMessageAsync += HandleAsync;
        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Compute queue error ({Source}, entity {Entity}).", args.ErrorSource, args.EntityPath);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("Compute worker listening on {Queue} (Phase 2: count and log only).", options.QueueName);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                var s = _counters.Snapshot();
                _logger.LogInformation("Compute worker: {Messages} message(s) handled, {Pending} pending row(s) seen, {Skipped} skipped (tenant switched off), {Failed} failed.",
                    s.Messages, s.PendingSeen, s.SkippedDisabled, s.Failed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task HandleAsync(ProcessSessionMessageEventArgs args)
    {
        var message = TryParse(args.Message.Body);
        if (message is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "Unreadable", "The body is not a ComputeQueueMessage.", args.CancellationToken);
            return;
        }

        try
        {
            if (!await _settings.IsComputeEnabledAsync(message.TenantId, args.CancellationToken))
            {
                _counters.SkippedDisabled();
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                return;
            }

            using var scope = _services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(message.TenantId);
            var processor = scope.ServiceProvider.GetRequiredService<ComputeTableProcessor>();
            try
            {
                var result = await processor.DrainAsync(message.TableId, ComputeConfig.ReadCompute(_configuration), args.CancellationToken);
                if (result.TablesToNotify.Count > 0)
                {
                    // Chained stored fields on other tables have follow-up rows; a lost message is re-sent by the relay.
                    try { await scope.ServiceProvider.GetRequiredService<IComputeQueuePublisher>().PublishBatchAsync(result.TablesToNotify.Select(t => new ComputeQueueMessage(message.TenantId, t)), args.CancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Compute: could not tell the queue about follow-up work; the relay will."); }
                }
                _counters.Message(result.RowsProcessed);
                if (result.RowsProcessed > 0)
                    _logger.LogInformation("Compute: tenant {TenantId} table {TableId}: {Rows} change(s) applied, {Fields} field recompute(s), {Values} value(s) written.",
                        message.TenantId, message.TableId, result.RowsProcessed, result.FieldsRecomputed, result.ValuesWritten);
            }
            catch (DatabaseBusyException ex)
            {
                // The rows stay in the outbox; the relay re-publishes them once they are due. Not a failure of the message.
                _counters.SkippedDisabled();
                _logger.LogInformation("Compute: tenant {TenantId} table {TableId} postponed: {Reason}", message.TenantId, message.TableId, ex.Message);
            }
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _counters.Failed();
            _logger.LogError(ex, "Compute: message for tenant {TenantId} table {TableId} failed; it will be retried.", message.TenantId, message.TableId);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    /// <summary>The message body, or null when it is not a readable ComputeQueueMessage.</summary>
    internal static ComputeQueueMessage? TryParse(BinaryData body)
    {
        try
        {
            var m = JsonSerializer.Deserialize<ComputeQueueMessage>(body.ToString());
            return m is { TenantId: > 0, TableId: > 0 } ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
