using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Configurations;

namespace PowerBase.Application.Pipelines;

// Shared by every run on this host. Queue limits bound runs; this separately bounds
// the additional database connections opened by parallel record iterations.
public sealed class PipelineLoopWorkerPool : IDisposable
{
    private readonly SemaphoreSlim _slots;
    public int LoopConcurrency { get; }
    public int HostConcurrency { get; }

    public PipelineLoopWorkerPool(IOptions<PipelineExecutionOptions> options,
        ILogger<PipelineLoopWorkerPool>? logger = null)
    {
        var processors = Environment.ProcessorCount;
        var memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        (LoopConcurrency, HostConcurrency) = CalculateCapacity(options.Value, processors, memoryBytes);
        _slots = new SemaphoreSlim(HostConcurrency, HostConcurrency);
        logger?.LogInformation("Pipeline loop workers: automatic={Automatic}, processors={Processors}, memory budget={MemoryBytes}, per loop={LoopWorkers}, per host={HostWorkers}.",
            options.Value.AutoScaleLoopWorkers, processors, memoryBytes, LoopConcurrency, HostConcurrency);
    }

    public static (int Loop, int Host) CalculateCapacity(PipelineExecutionOptions options,
        int processorCount, long memoryBytes)
    {
        if (!options.AutoScaleLoopWorkers)
            return (options.LoopConcurrency, options.PerInstanceLoopConcurrency);

        // ProcessorCount honors the process/container CPU allocation. The GC budget
        // honors container/GC memory limits. Reserve 1 GiB and half the remainder for
        // the API and other jobs, then budget 256 MiB per active record worker.
        const long gib = 1024L * 1024 * 1024;
        var memorySlots = memoryBytes <= gib ? 1L : Math.Max(1L, (memoryBytes - gib) / (gib / 2));
        var cpuSlots = Math.Clamp(processorCount, 1, 32);
        var host = (int)Math.Max(1, Math.Min(options.PerInstanceLoopConcurrency,
            Math.Min(cpuSlots * 2L, memorySlots)));
        var loop = Math.Max(1, Math.Min(options.LoopConcurrency, Math.Min(cpuSlots, host)));
        return (loop, host);
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        return new Lease(_slots);
    }

    public void Dispose() => _slots.Dispose();

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
