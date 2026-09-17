using System;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Infrastructure.Pipelines;

/// <summary>Coalesces notifications and retains a wake received while the worker is busy.</summary>
public sealed class PipelineWakeSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    public void Wake()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* A wake is already pending. */ }
    }

    public void Dispose() => _signal.Dispose();
}
