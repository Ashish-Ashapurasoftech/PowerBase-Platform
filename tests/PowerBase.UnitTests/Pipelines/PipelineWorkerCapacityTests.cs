using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineWorkerCapacityTests
{
    private static long _tenantId = 900000;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ClaimsOnlyFreeSlotsAcrossReclaimedAndPendingJobs(int reclaimedCount)
    {
        var tenantId = Interlocked.Increment(ref _tenantId);
        var queue = Substitute.For<IMainPipelineQueueRepository>();
        var pipelines = Substitute.For<IPipelineRepository>();
        var held = new TaskCompletionSource<Pipeline?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipelines.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(held.Task);
        var jobs = Enumerable.Range(1, 2).Select(id => new PipelineQueue
        {
            Id = id, PublicId = Guid.NewGuid(), TenantId = tenantId, PipelineId = id,
            ClaimToken = Guid.NewGuid(), Status = "Processing", CreatedOn = DateTime.UtcNow
        }).ToArray();
        queue.ReclaimExpiredJobsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<List<long>>(), Arg.Any<CancellationToken>())
            .Returns(jobs.Take(reclaimedCount).ToArray());
        queue.ClaimPendingJobsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<List<long>>(), Arg.Any<CancellationToken>())
            .Returns(jobs.Skip(reclaimedCount).ToArray());
        using var provider = new ServiceCollection()
            .AddSingleton(queue).AddSingleton(pipelines).AddSingleton(Substitute.For<IQueryContext>()).BuildServiceProvider();
        using var worker = new DatabasePipelineExecutionWorker(provider, Substitute.For<IControlConnectionFactory>(),
            Options.Create(new PipelineExecutionOptions { PerInstanceTenantConcurrencyLimit = 2 }),
            NullLogger<DatabasePipelineExecutionWorker>.Instance);
        var dispatch = typeof(DatabasePipelineExecutionWorker).GetMethod("DispatchAvailableJobsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var active = (ConcurrentDictionary<string, Task>)typeof(DatabasePipelineExecutionWorker)
            .GetField("_activeTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        try
        {
            await (Task)dispatch.Invoke(worker, [new List<long> { tenantId }, CancellationToken.None])!;
            Assert.Equal(2, active.Count);
            await queue.Received(1).ReclaimExpiredJobsAsync(Arg.Any<string>(), 2, Arg.Any<int>(),
                Arg.Is<List<long>>(ids => ids.SequenceEqual(new[] { tenantId })), Arg.Any<CancellationToken>());
            if (reclaimedCount < 2)
                await queue.Received(1).ClaimPendingJobsAsync(Arg.Any<string>(), 2 - reclaimedCount, Arg.Any<int>(), Arg.Any<List<long>>(), Arg.Any<CancellationToken>());
            else
                await queue.DidNotReceive().ClaimPendingJobsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<List<long>>(), Arg.Any<CancellationToken>());

            queue.ClearReceivedCalls();
            await (Task)dispatch.Invoke(worker, [new List<long> { tenantId }, CancellationToken.None])!;
            Assert.Empty(queue.ReceivedCalls());
        }
        finally
        {
            var running = active.Values.ToArray();
            held.TrySetResult(new Pipeline { IsDeleted = true });
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Empty(active);
    }
}
