using System.Text.Json;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Workers;

/// <summary>
/// Optional one-shot: when the setting DecryptDatabase:Database is set (e.g. app setting DecryptDatabase__Database=Powerbase_17),
/// the API decrypts that tenant database in the background right after it starts, without anyone having to call
/// POST admin/encryption/decrypt-database. Same code, same safety rules, same single-run lock.
///
///   DecryptDatabase:Database          database name; empty/missing = this worker does nothing (the normal case)
///   DecryptDatabase:DryRun            default TRUE — only an explicit "false" decrypts for real
///   DecryptDatabase:TableParallelism  default 4
///
/// Progress and the final result are written to the log as "[DecryptDatabase] …" lines.
/// Remove the setting when the run is finished, otherwise every restart looks at the database again
/// (harmless — already-plain columns are skipped — but pointless).
/// </summary>
public class DecryptDatabaseOnStartupWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DecryptionJobTracker _tracker;
    private readonly ILogger<DecryptDatabaseOnStartupWorker> _logger;

    public DecryptDatabaseOnStartupWorker(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        DecryptionJobTracker tracker,
        ILogger<DecryptDatabaseOnStartupWorker> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var database = _configuration["DecryptDatabase:Database"]?.Trim();
        if (string.IsNullOrEmpty(database)) return;

        var dryRun = !string.Equals(_configuration["DecryptDatabase:DryRun"]?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        var parallelism = int.TryParse(_configuration["DecryptDatabase:TableParallelism"], out var p) ? Math.Clamp(p, 1, 16) : 4;

        try
        {
            // Let the API finish starting (and migrations/seeding settle) before touching a big database.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            var job = new DecryptDatabaseResult { Database = database, DryRun = dryRun };
            if (!_tracker.TryStart(job))
            {
                _logger.LogWarning("[DecryptDatabase] A run for {Database} is already going; the startup run is skipped.", database);
                return;
            }

            _logger.LogInformation("[DecryptDatabase] Startup run requested: database={Database} dryRun={DryRun} tableParallelism={Parallelism}",
                database, dryRun, parallelism);

            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDatabaseDecryptionService>();
            var run = service.RunAsync(job, parallelism, CancellationToken.None);

            while (!run.IsCompleted)
            {
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30), stoppingToken));
                if (run.IsCompleted) break;
                LogProgress(job);
            }
            await run;

            string json;
            lock (job.Sync) json = JsonSerializer.Serialize(job, JsonOptions);
            _logger.LogInformation("[DecryptDatabase] RESULT {Json}", json);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[DecryptDatabase] Startup run stopped because the API is shutting down; start it again to continue (finished columns are skipped).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DecryptDatabase] Startup run failed for {Database}", database);
        }
    }

    private void LogProgress(DecryptDatabaseResult job)
    {
        lock (job.Sync)
        {
            _logger.LogInformation(
                "[DecryptDatabase] PROGRESS {Database} state={State} apps={AppsDecrypted}/{AppsScanned} tables={Tables} fields found={Found} ok={Ok} failed={Failed} rows={Rows} table={Table}",
                job.Database, job.State, job.AppsDecrypted, job.AppsScanned, job.TablesScanned,
                job.FieldsFound, job.FieldsDecrypted, job.FieldsFailed, job.RowsDecrypted, job.CurrentTable);
        }
    }
}
