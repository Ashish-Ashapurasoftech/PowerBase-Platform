using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Jobs;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Workers;

/// <summary>Runs background app imports (files over 3 MB) one at a time, off the request thread, with the rights of the person
/// who uploaded the file. A restart loses queued work; the job list marks such jobs failed so nobody waits on them.</summary>
public sealed class AppImportJobWorker(IServiceProvider services, AppImportJobQueue queue, ILogger<AppImportJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = services.CreateScope();
                    var context = (QueryContext)scope.ServiceProvider.GetRequiredService<IQueryContext>();
                    context.SetTenantId(request.TenantId);
                    context.SetUserIdentity(request.UserId, request.IsSuperAdmin, request.UserName, request.UserEmail, request.Permissions, request.TenantRole);
                    await scope.ServiceProvider.GetRequiredService<AppImportJobRunner>().RunAsync(request.JobPublicId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background app import {Job} crashed.", request.JobPublicId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
