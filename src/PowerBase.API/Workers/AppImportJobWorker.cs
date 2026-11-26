using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Jobs;
using PowerBase.Infrastructure.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>
/// Runs background app imports (files over 3 MB) one at a time per instance, off the request thread, with the rights of the person
/// who uploaded the file. Jobs come from the durable control-DB queue (meta.AppImportQueue): any instance may run a job queued on any
/// other, queued jobs survive restarts and scale-in, and a lease (renewed while the import runs) keeps two instances off one job.
/// A job whose instance stopped mid-import (its lease expired while Processing) is marked failed, not run again: the half-done first
/// attempt may already have created an app, and a second run would create a duplicate.
/// </summary>
public sealed class AppImportJobWorker(IServiceProvider services, ILogger<AppImportJobWorker> logger) : BackgroundService
{
    private const int LeaseSeconds = 120;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            AppImportQueueItem? item = null;
            try
            {
                using (var scope = services.CreateScope())
                    item = await scope.ServiceProvider.GetRequiredService<IAppImportDispatchQueue>().ClaimAsync(_workerId, LeaseSeconds, stoppingToken);
                if (item is null)
                {
                    await AppImportQueueWake.WaitAsync(PollInterval, stoppingToken);
                    continue;
                }
                await RunAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Queue unreachable (migration not applied yet, database down): wait and try again.
                logger.LogError(ex, "Background app import {Job} could not be run.", item?.JobPublicId);
                try { await Task.Delay(PollInterval, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunAsync(AppImportQueueItem item, CancellationToken stoppingToken)
    {
        using var scope = services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IAppImportDispatchQueue>();
        var identity = AppImportIdentity.FromJson(item.IdentityJson);
        var context = (QueryContext)scope.ServiceProvider.GetRequiredService<IQueryContext>();
        context.SetTenantId(item.TenantId);
        context.SetUserIdentity(identity.UserId, identity.IsSuperAdmin, identity.UserName, identity.UserEmail,
            new HashSet<string>(identity.Permissions), identity.TenantRole);

        if (item.AttemptCount > 1)
        {
            // Its first instance stopped mid-import: never run twice (a duplicate app); say so and let the user upload again.
            var jobs = scope.ServiceProvider.GetRequiredService<IAppImportJobRepository>();
            var job = await jobs.GetByPublicIdAsync(item.JobPublicId, stoppingToken);
            if (job is not null && job.Status is AppImportJobStatus.Queued or AppImportJobStatus.Running)
                await jobs.FailAsync(job.Id, "The import was interrupted because its server stopped. Please upload the file again.", CancellationToken.None);
            await queue.CompleteAsync(item.Id, _workerId, item.ClaimToken, CancellationToken.None);
            logger.LogWarning("Background app import {Job} was interrupted on another instance; marked failed.", item.JobPublicId);
            return;
        }

        // Keep the lease while the import runs; losing it (database unreachable for the whole lease) cancels the run so it is not
        // finished by an instance that no longer owns it.
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renew = Task.Run(async () =>
        {
            while (!leaseLost.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(LeaseSeconds / 3), leaseLost.Token); }
                catch (OperationCanceledException) { return; }
                bool renewed;
                try { renewed = await queue.RenewLeaseAsync(item.Id, _workerId, item.ClaimToken, LeaseSeconds, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Lease of app import {Job} not renewed this time.", item.JobPublicId); continue; }
                if (!renewed)
                {
                    logger.LogError("Lease of app import {Job} was lost; stopping it.", item.JobPublicId);
                    leaseLost.Cancel();
                    return;
                }
            }
        });

        try
        {
            await scope.ServiceProvider.GetRequiredService<AppImportJobRunner>().RunAsync(item.JobPublicId, leaseLost.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-import: the lease runs out and the next instance marks the job interrupted.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background app import {Job} crashed.", item.JobPublicId);
        }
        finally
        {
            leaseLost.Cancel();
            await renew;
        }
        await queue.CompleteAsync(item.Id, _workerId, item.ClaimToken, CancellationToken.None);
    }
}
