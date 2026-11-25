using PowerBase.Application.AppLogs;
using PowerBase.Infrastructure.AppLogs;

namespace PowerBase.API.Workers;

/// <summary>Writes the application log rows queued by AppLogMiddleware / AppLogCollector to the logs database in batches
/// (up to 500 rows, or whatever arrived within a second). When the logs database cannot be reached the batch is dropped and
/// a warning logged (at most once a minute), so the API never stalls or grows memory because of its log.</summary>
public sealed class AppLogWriterWorker(AppLogQueue queue, IAppLogRepository repository, ILogger<AppLogWriterWorker> logger) : BackgroundService
{
    private const int BatchSize = 500;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(1);
    private DateTime _lastWarning = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AppLogEntry>(BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await queue.Reader.WaitToReadAsync(stoppingToken)) break;
                var windowEnds = DateTime.UtcNow + BatchWindow;
                while (batch.Count < BatchSize)
                {
                    if (queue.Reader.TryRead(out var entry)) { batch.Add(entry); continue; }
                    var left = windowEnds - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wait.CancelAfter(left);
                    try { if (!await queue.Reader.WaitToReadAsync(wait.Token)) break; }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { break; }
                }
                await repository.InsertManyAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - _lastWarning > TimeSpan.FromMinutes(1))
                {
                    _lastWarning = DateTime.UtcNow;
                    logger.LogWarning(ex, "Application log: {Count} row(s) could not be written to the logs database (run the Migrator's \"migrate logs\"?)", batch.Count);
                }
            }
            finally
            {
                batch.Clear();
            }
        }
    }
}
