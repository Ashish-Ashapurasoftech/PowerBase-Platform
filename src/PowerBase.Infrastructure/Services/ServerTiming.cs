using System.Diagnostics;
using System.Globalization;
using System.Text;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Services;

/// <summary>Collects the step timings of one request (scoped). Steps that run in parallel are each timed on their own, so they can add up to
/// more than the request took.</summary>
public sealed class ServerTiming : IServerTiming
{
    private readonly object _gate = new();
    private readonly List<(string Name, double Ms)> _entries = [];

    public IDisposable Measure(string name) => new Scope(this, name);

    public void Add(string name, TimeSpan duration)
    {
        lock (_gate) _entries.Add((name, duration.TotalMilliseconds));
    }

    /// <summary>The Server-Timing header value, e.g. <c>rows;dur=412.3, count;dur=88.1</c>; empty when nothing was timed.</summary>
    public string ToHeaderValue()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            foreach (var (name, ms) in _entries)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name).Append(";dur=").Append(ms.ToString("0.0", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly ServerTiming _owner;
        private readonly string _name;
        private readonly long _start = Stopwatch.GetTimestamp();

        public Scope(ServerTiming owner, string name) { _owner = owner; _name = name; }

        public void Dispose() => _owner.Add(_name, Stopwatch.GetElapsedTime(_start));
    }
}
