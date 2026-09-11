using System;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Infrastructure.Pipelines;

public static class DatabasePipelineQueueWakeNotifier
{
    private static readonly PipelineWakeSignal Signal = new();

    public static Task WaitForJobAsync(CancellationToken ct) => Signal.WaitAsync(Timeout.InfiniteTimeSpan, ct);
    public static Task WaitForJobAsync(TimeSpan timeout, CancellationToken ct) => Signal.WaitAsync(timeout, ct);
    public static void Wake() => Signal.Wake();
}
