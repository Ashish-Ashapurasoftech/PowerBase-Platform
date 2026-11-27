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
    private readonly List<(string Name, double Ms, string? Desc)> _entries = [];

    public IDisposable Measure(string name) => new Scope(this, name);

    public void Add(string name, TimeSpan duration)
    {
        lock (_gate) _entries.Add((name, duration.TotalMilliseconds, null));
    }

    /// <summary>A step with a short description (e.g. a count: <c>cache-sql;dur=42.0;desc="3"</c>). The description must be a plain token:
    /// quotes and backslashes are dropped.</summary>
    public void Add(string name, TimeSpan duration, string description)
    {
        var desc = new string(description.Where(c => c is not ('"' or '\\') && !char.IsControl(c)).ToArray());
        lock (_gate) _entries.Add((name, duration.TotalMilliseconds, desc));
    }

    /// <summary>The Server-Timing header value, e.g. <c>rows;dur=412.3, count;dur=88.1</c>; empty when nothing was timed.</summary>
    public string ToHeaderValue()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            foreach (var (name, ms, desc) in _entries)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name).Append(";dur=").Append(ms.ToString("0.0", CultureInfo.InvariantCulture));
                if (desc is not null) sb.Append(";desc=\"").Append(desc).Append('"');
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
