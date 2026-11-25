using System.Threading.Channels;
using PowerBase.Application.AppLogs;

namespace PowerBase.Infrastructure.AppLogs;

/// <summary>Singleton in-memory queue between requests and the AppLogWriterWorker. Bounded: if the logs database is down
/// for long enough to fill it, new rows are dropped rather than holding memory or slowing requests.</summary>
public sealed class AppLogQueue : IAppLogQueue
{
    private readonly Channel<AppLogEntry> _channel = Channel.CreateBounded<AppLogEntry>(
        new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public ChannelReader<AppLogEntry> Reader => _channel.Reader;

    public void Enqueue(AppLogEntry entry) => _channel.Writer.TryWrite(entry);
}
