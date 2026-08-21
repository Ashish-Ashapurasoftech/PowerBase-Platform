using System;
using System.Threading;
using System.Threading.Tasks;

namespace PowerBase.Infrastructure.Pipelines;

public static class DatabasePipelineQueueWakeNotifier
{
    private static TaskCompletionSource<bool>? _wakeTcs;
    private static readonly object _lock = new();

    public static Task WaitForJobAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_wakeTcs == null || _wakeTcs.Task.IsCompleted)
            {
                _wakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return _wakeTcs.Task.WaitAsync(ct);
        }
    }

    public static void Wake()
    {
        lock (_lock)
        {
            _wakeTcs?.TrySetResult(true);
        }
    }
}
