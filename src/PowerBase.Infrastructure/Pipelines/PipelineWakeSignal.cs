using System;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Infrastructure.Pipelines;

public sealed class PipelineWakeSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    public void Wake()
    {
        // A wake is already pending: nothing to do. Checked first because releasing a full semaphore THROWS, and every record
        // written by a Copy Records page calls this — hundreds of first-chance exceptions per commit (~30 ms each under a debugger).
        if (_signal.CurrentCount > 0) return;
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* A wake is already pending. */ }
    }

    public void Dispose() => _signal.Dispose();
}
