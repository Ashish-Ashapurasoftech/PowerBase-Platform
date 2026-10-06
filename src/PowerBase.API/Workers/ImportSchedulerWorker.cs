using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>Starts imports on their schedule. Once a minute it asks every tenant which saved imports have come due and queues each one
/// as the person it runs as, through the same start path as the Run button, so a scheduled run is checked, limited and reported
/// exactly like a manual one. A per-tenant database lock keeps two API instances from both starting the same occurrence.</summary>
public sealed class ImportSchedulerWorker(
    IControlConnectionFactory control, IServiceProvider services, IConfiguration configuration, ILogger<ImportSchedulerWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Import scheduler loop failed."); }
        }
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        List<long> tenantIds;
        await using (var conn = control.Create())
        {
            await conn.OpenAsync(ct);
            tenantIds = (await conn.QueryAsync<long>(new CommandDefinition(
                "SELECT Id FROM meta.Tenant WHERE IsDeleted = 0 AND ProvisioningState = 'Ready'", cancellationToken: ct))).ToList();
        }

        foreach (var tenantId in tenantIds)
        {
            if (ct.IsCancellationRequested) break;
            try { await EvaluateTenantAsync(tenantId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Import scheduling failed for tenant {TenantId}.", tenantId); }
        }
    }

    private async Task EvaluateTenantAsync(long tenantId, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        ((QueryContext)scope.ServiceProvider.GetRequiredService<IQueryContext>()).SetTenantId(tenantId);

        await using var lockConn = await scope.ServiceProvider.GetRequiredService<ITenantConnectionFactory>().CreateAsync(ct);
        await lockConn.OpenAsync(ct);
        var locked = await lockConn.QuerySingleAsync<int>(
            "DECLARE @res INT; EXEC @res = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @res;",
            new { name = $"PB_ImportScheduler_Tenant_{tenantId}" });
        if (locked < 0) return; // another instance is evaluating this tenant

        var dispatcher = scope.ServiceProvider.GetRequiredService<ImportScheduleDispatcher>();
        var now = DateTime.UtcNow;
        // Uploaded files nobody imported are not kept past a day.
        await scope.ServiceProvider.GetRequiredService<PowerBase.Application.Imports.Files.ImportFileCleanup>().RunAsync(now, ct);
        var due = await dispatcher.ListDueAsync(now, ct);
        foreach (var def in due)
        {
            if (ct.IsCancellationRequested) break;
            try { await DispatchAsync(tenantId, dispatcher, def, now, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Scheduled import {ImportId} could not be dispatched.", def.PublicId); }
        }
    }

    private async Task DispatchAsync(long tenantId, ImportScheduleDispatcher dispatcher, PowerBase.Domain.Entities.ImportDefinition def, DateTime now, CancellationToken ct)
    {
        var outcome = await dispatcher.ClaimAsync(def, now, ct);
        if (outcome == ImportDispatchOutcome.SkippedOverlap)
            logger.LogInformation("Scheduled import {ImportId} skipped: the previous run is still going.", def.PublicId);
        if (outcome != ImportDispatchOutcome.Claimed) return;

        // The run belongs to the person the import runs as, with their access as it is now, not as it was when they saved it.
        using var runScope = services.CreateScope();
        var sp = runScope.ServiceProvider;
        var context = (QueryContext)sp.GetRequiredService<IQueryContext>();
        context.SetTenantId(tenantId);
        var user = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(def.RunAsUserId, ct);
        if (user is null || !user.IsActive || user.IsDeleted)
        {
            await dispatcher.RecordFailedRunAsync(def, "The user this import runs as is no longer active. Open the import and save it again to run as you.", ct);
            return;
        }
        context.SetUserIdentity(user.Id, user.SystemRoleCode == SystemRoleCodes.SuperAdmin, user.Name, user.Email, new HashSet<string>(), string.Empty);

        try
        {
            await sp.GetRequiredService<StartImportRunHandler>()
                .HandleAsync(def.PublicId, ImportTrigger.Schedule, null, configuration["Frontend:BaseUrl"], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything that stops a manual start (access lost, a mapped field gone, a duplicate merge key) also stops a scheduled one.
            logger.LogWarning(ex, "Scheduled import {ImportId} could not start.", def.PublicId);
            await dispatcher.RecordFailedRunAsync(def, ex.Message, ct);
        }
    }
}
