using Dapper;
using PowerBase.Application.Formulas;
using PowerBase.Formula;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.API.Services;

/// <summary>
/// Warms a new instance before the load balancer sends it traffic (doc 13 §3, platform level): opens the control-database connection
/// pool, builds the request-path services once (DI graph, JIT of their constructors) and compiles a formula (formula engine JIT).
/// <see cref="ReadinessHealthCheck"/> answers Healthy only after this finished — or gave up after <see cref="Limit"/>, so a slow warm-up
/// never keeps an instance out of rotation for good. No tenant data is read.
/// </summary>
public sealed class StartupWarmup(IServiceProvider services, ILogger<StartupWarmup> logger) : BackgroundService
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static int _done;

    /// <summary>True once warm-up finished (or timed out / failed — it is only a speed-up).</summary>
    public static bool Completed => Volatile.Read(ref _done) == 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            limit.CancelAfter(Limit);
            await using (var connection = services.GetRequiredService<IControlConnectionFactory>().Create())
            {
                await connection.OpenAsync(limit.Token);
                await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: limit.Token));
            }

            using (var scope = services.CreateScope())
            {
                foreach (var type in new[]
                {
                    typeof(PowerBase.Application.Reports.Queries.RunReport.RunReportQueryHandler),
                    typeof(PowerBase.Application.Records.Queries.ListRecords.ListRecordsQueryHandler),
                    typeof(PowerBase.Application.Relationships.Queries.GetParentOptionsQueryHandler),
                    typeof(PowerBase.Application.Records.Queries.GetDistinctFieldValues.GetDistinctFieldValuesQueryHandler),
                })
                {
                    try { scope.ServiceProvider.GetService(type); }
                    catch (Exception ex) { logger.LogDebug(ex, "Warm-up could not build {Type}", type.Name); }
                }
            }

            var engine = services.GetService<FormulaEngine>() ?? new FormulaEngine();
            CompiledFormulaCache.GetOrCompile(engine, "If(1 > 0, \"warm\", \"cold\")", new AppFieldSchema([]), PowerBase.Formula.Types.FormulaType.Text);
            logger.LogInformation("Startup warm-up done in {Ms} ms", clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Startup warm-up did not finish in {Ms} ms; the instance takes traffic anyway", clock.ElapsedMilliseconds);
        }
        finally
        {
            Volatile.Write(ref _done, 1);
        }
    }
}
