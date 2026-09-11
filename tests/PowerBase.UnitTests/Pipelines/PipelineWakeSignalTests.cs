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
