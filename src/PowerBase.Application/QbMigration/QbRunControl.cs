namespace PowerBase.Application.QbMigration;

/// <summary>What an operator asked a running migration to do (qbm.Run.Control).</summary>
public enum QbRunCommand { Run, Pause, Stop }

/// <summary>The gate a worker passes before it takes the next chunk. An operator sets "pause" or "stop" on the run
/// (worker commands pause / resume / stop); the chunk being written always finishes first, because a chunk is one
/// transaction. Paused: the gate waits until resumed or stopped. Stopped: the gate says no, and the remaining chunks
/// stay pending for the next start. The flag is read at most once per poll interval, however many workers ask.</summary>
public sealed class QbRunControl(Func<CancellationToken, Task<string?>> readFlag, TimeSpan poll, Action<QbRunCommand>? changed = null)
{
    private readonly SemaphoreSlim _read = new(1, 1);
    private QbRunCommand _current = QbRunCommand.Run;
    private DateTime _readAt = DateTime.MinValue;

    public bool Stopped => _current == QbRunCommand.Stop;

    public static QbRunCommand Parse(string? flag) => flag?.Trim().ToLowerInvariant() switch
    {
        "pause" => QbRunCommand.Pause,
        "stop" => QbRunCommand.Stop,
        _ => QbRunCommand.Run,
    };

    /// <summary>The operator's current request, read again when the last read is older than the poll interval.</summary>
    public async Task<QbRunCommand> CurrentAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _readAt < poll) return _current;
        await _read.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _readAt < poll) return _current;
            var next = Parse(await readFlag(ct));
            _readAt = DateTime.UtcNow;
            if (next != _current)
            {
                _current = next;
                changed?.Invoke(next);
            }
            return _current;
        }
        finally
        {
            _read.Release();
        }
    }

    /// <summary>True: take the next chunk. False: stop was asked; leave the rest for the next start. Waits while paused.</summary>
    public async Task<bool> WaitUntilAllowedAsync(CancellationToken ct)
    {
        while (true)
        {
            switch (await CurrentAsync(ct))
            {
                case QbRunCommand.Run: return true;
                case QbRunCommand.Stop: return false;
                default: await Task.Delay(poll, ct); break;
            }
        }
    }
}
