using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineLoopWorkerPoolTests
{
    [Theory]
    [InlineData(1, 2, 1, 2)]
    [InlineData(2, 4, 2, 4)]
    [InlineData(3, 4, 3, 6)]
    [InlineData(6, 8, 6, 12)]
    [InlineData(8, 16, 8, 16)]
    [InlineData(32, 64, 16, 32)]
    [InlineData(16, 2, 2, 2)]
    [InlineData(0, 0, 1, 1)]
    public void AutomaticCapacity_RespectsCpuMemoryAndCeilings(int cpu, int gib, int loop, int host)
    {
        var options = new PipelineExecutionOptions {
            AutoScaleLoopWorkers = true, LoopConcurrency = 16, PerInstanceLoopConcurrency = 32
        };
        Assert.Equal((loop, host), PipelineLoopWorkerPool.CalculateCapacity(options, cpu, gib * 1024L * 1024 * 1024));
    }

    [Fact]
    public void ManualMode_PreservesExplicitLimits()
    {
        Assert.Equal((4, 16), PipelineLoopWorkerPool.CalculateCapacity(new(), 1, 0));
    }

    [Fact]
    public async Task SharedPool_BlocksAtCapacity_CancellationDoesNotLeakPermits()
    {
        using var pool = new PipelineLoopWorkerPool(Options.Create(new PipelineExecutionOptions {
            AutoScaleLoopWorkers = true, LoopConcurrency = 1, PerInstanceLoopConcurrency = 1
        }));
        using var first = await pool.AcquireAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = pool.AcquireAsync(cancel.Token);
        Assert.False(waiting.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        first.Dispose(); // A repeated disposal must not manufacture extra capacity.
        using var next = await pool.AcquireAsync(CancellationToken.None);
        Assert.Equal(1, pool.HostConcurrency);
        Assert.Equal(1, pool.LoopConcurrency);
    }
}
