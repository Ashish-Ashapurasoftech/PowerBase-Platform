namespace PowerBase.Application.Common;

/// <summary>How far behind a read replica may be for the work in a <see cref="ReadReplicaScope"/>.</summary>
public enum ReadReplicaLane
{
    /// <summary>A user is waiting (export, summary / chart report, report totals): ReadReplica:MaxLagSeconds.</summary>
    Interactive = 0,
    /// <summary>Background work (search re-indexing pages): ReadReplica:BackgroundMaxLagSeconds.</summary>
    Background = 1
}

/// <summary>
/// Opt-in marker: "the data reads made inside this scope may come from the tenant's read replica". Outside a scope nothing changes — every
/// read stays on the primary. Only the heavy, lag-tolerant data reads of the repositories look at it (report list / count / totals /
/// summarize and the search-index table pages); saves, transactions, metadata, permission checks and unique/required checks never do.
/// With ReadReplica:Enabled off (the default) or the tenant's replica off, a read inside a scope is still a primary read.
/// <code>using (ReadReplicaScope.Begin(ReadReplicaLane.Interactive)) { … }</code>
/// </summary>
public static class ReadReplicaScope
{
    private static readonly AsyncLocal<ReadReplicaLane?> Lane = new();

    /// <summary>The lane of the scope the current async flow is in; null = no scope (primary).</summary>
    public static ReadReplicaLane? Current => Lane.Value;

    public static IDisposable Begin(ReadReplicaLane lane)
    {
        var previous = Lane.Value;
        Lane.Value = lane;
        return new Restore(previous);
    }

    private sealed class Restore(ReadReplicaLane? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Lane.Value = previous;
        }
    }
}
