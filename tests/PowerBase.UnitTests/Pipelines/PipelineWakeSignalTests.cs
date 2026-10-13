using PowerBase.Infrastructure.Pipelines;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineWakeSignalTests
{
    [Fact]
    public async Task WakeWhileBusyIsRememberedAndCoalesced()
    {
        using var signal = new PipelineWakeSignal();
        for (var i = 0; i < 100; i++) signal.Wake();
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task IdleTimeoutsDoNotLeaveWaitersThatConsumeFutureNotifications()
    {
        using var signal = new PipelineWakeSignal();
        for (var i = 0; i < 100; i++)
            Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        var waiting = signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        signal.Wake();
        Assert.True(await waiting);
    }

    [Fact]
    public async Task ManyWakesWhileOneIsPending_ThrowNothing_NotEvenFirstChance()
    {
        // Copy Records raised a wake per created record; releasing an already-full semaphore throws, and under a debugger each of those
        // exceptions cost ~30 ms. The signal must be coalesced WITHOUT an exception being thrown and caught.
        using var signal = new PipelineWakeSignal();
        var thrown = 0;
        void Handler(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is SemaphoreFullException) Interlocked.Increment(ref thrown);
        }
        AppDomain.CurrentDomain.FirstChanceException += Handler;
        try
        {
            for (var i = 0; i < 2000; i++) signal.Wake();
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Handler; }

        Assert.Equal(0, thrown);
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));    // one wake is pending
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));   // and only one
    }

    [Fact]
    public async Task AWakeAfterTheSignalWasConsumedIsStillDelivered()
    {
        using var signal = new PipelineWakeSignal();
        signal.Wake();
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));

        signal.Wake();

        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentWakesNeverLoseTheWakeAndNeverThrow()
    {
        using var signal = new PipelineWakeSignal();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => { for (var i = 0; i < 5000; i++) signal.Wake(); })));

        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotConsumeNextWake()
    {
        using var signal = new PipelineWakeSignal();
        using var cts = new CancellationTokenSource();
        var waiting = signal.WaitAsync(Timeout.InfiniteTimeSpan, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        signal.Wake();
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }
}
