using Microsoft.Extensions.Caching.Memory;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Infrastructure.Services;

/// <summary>In-memory <see cref="IAppCopyProgressStore"/>. Entries expire on their own, so an abandoned dialog leaves nothing
/// behind. With several API instances only the one running the copy knows its progress; the dialog then shows its own
/// estimate instead.</summary>
public sealed class AppCopyProgressStore(IMemoryCache cache) : IAppCopyProgressStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private const string DONE_STAGE = "Done";
    private const string FAILED_STAGE = "Failed";

    private sealed record Entry(long UserId, AppCopyProgress Progress);

    private readonly object _gate = new();

    public void Report(Guid progressId, long userId, int percent, string stage)
    {
        lock (_gate)
        {
            var current = Read(progressId);
            if (current is not null && (current.UserId != userId || current.Progress.IsDone)) return;
            var value = Math.Clamp(percent, 0, 99);
            if (current is not null && value < current.Progress.Percent) value = current.Progress.Percent;
            Write(progressId, new Entry(userId, new AppCopyProgress(value, stage, IsDone: false, IsFailed: false)));
        }
    }

    public void Complete(Guid progressId, long userId, bool failed)
    {
        lock (_gate)
        {
            var current = Read(progressId);
            if (current is not null && current.UserId != userId) return;
            var percent = failed ? current?.Progress.Percent ?? 0 : 100;
            Write(progressId, new Entry(userId, new AppCopyProgress(percent, failed ? FAILED_STAGE : DONE_STAGE, IsDone: true, IsFailed: failed)));
        }
    }

    public AppCopyProgress? Get(Guid progressId, long userId)
    {
        var entry = Read(progressId);
        return entry is not null && entry.UserId == userId ? entry.Progress : null;
    }

    private Entry? Read(Guid progressId) => cache.Get<Entry>(Key(progressId));

    private void Write(Guid progressId, Entry entry) => cache.Set(Key(progressId), entry, Lifetime);

    private static string Key(Guid progressId) => $"app-copy-progress:{progressId}";
}
