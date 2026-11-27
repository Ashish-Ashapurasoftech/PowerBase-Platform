using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// <see cref="IAppCopyProgressStore"/>: the instance running the copy keeps the progress in memory (the source of truth: the copy is
/// one request on one instance) and, when Redis is configured (MetadataCache:Redis — the same IDistributedCache as the metadata L2),
/// mirrors every step there, so the dialog's polls get the real progress from whichever API instance answers them. Without Redis it
/// is the in-memory store it always was. Entries expire on their own, so an abandoned dialog leaves nothing behind.
/// </summary>
public sealed class AppCopyProgressStore(IMemoryCache cache, IDistributedCache? redis = null, ILogger<AppCopyProgressStore>? logger = null)
    : IAppCopyProgressStore
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
        var entry = Read(progressId) ?? ReadShared(progressId);
        return entry is not null && entry.UserId == userId ? entry.Progress : null;
    }

    private Entry? Read(Guid progressId) => cache.Get<Entry>(Key(progressId));

    private void Write(Guid progressId, Entry entry)
    {
        cache.Set(Key(progressId), entry, Lifetime);
        if (redis is null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        // Not awaited: the copy must never wait for (or fail on) the progress mirror. Chained (callers hold _gate), so the mirror's
        // writes land in order and "Done" is never overwritten by an earlier step.
        _mirror = _mirror.ContinueWith(async _ =>
        {
            try
            {
                await redis.SetAsync(Key(progressId), bytes,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime });
            }
            catch (Exception ex) { logger?.LogDebug(ex, "App copy progress {ProgressId} could not be written to Redis", progressId); }
        }, TaskScheduler.Default).Unwrap();
    }

    private Task _mirror = Task.CompletedTask;

    /// <summary>Another instance's copy (this one has no entry): the Redis mirror, if any. A Redis problem only means "no progress
    /// known" — the dialog then shows its own estimate, as before.</summary>
    private Entry? ReadShared(Guid progressId)
    {
        if (redis is null) return null;
        try
        {
            var bytes = redis.Get(Key(progressId));
            return bytes is { Length: > 0 } ? JsonSerializer.Deserialize<Entry>(bytes) : null;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "App copy progress {ProgressId} could not be read from Redis", progressId);
            return null;
        }
    }

    private static string Key(Guid progressId) => $"app-copy-progress:{progressId}";
}
