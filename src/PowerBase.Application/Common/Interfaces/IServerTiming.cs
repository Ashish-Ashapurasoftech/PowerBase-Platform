namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Per-request step timings, sent to the caller as the Server-Timing response header (visible in the browser's Network tab, Timing).
/// A no-op when the header is switched off, so measuring a step costs nothing in production.
/// </summary>
public interface IServerTiming
{
    /// <summary>Times the work until the returned scope is disposed: <c>using (timing.Measure("rows")) { ... }</c>.</summary>
    IDisposable Measure(string name);

    /// <summary>Records a step that was timed elsewhere.</summary>
    void Add(string name, TimeSpan duration);
}

/// <summary>Null object for callers (tests, background work) that run without a request.</summary>
public sealed class NoServerTiming : IServerTiming
{
    public static readonly NoServerTiming Instance = new();
    private sealed class Scope : IDisposable { public void Dispose() { } }
    private static readonly Scope Empty = new();

    public IDisposable Measure(string name) => Empty;
    public void Add(string name, TimeSpan duration) { }
}
