using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Imports;

/// <summary>Deletes what a run kept once its retention has ended: its details files and the file that was uploaded for it. The run's counts and
/// its place in the history stay; only the files go, and the run is marked so the history says they have expired. A file that cannot be deleted
/// now (storage unreachable) is left for the next pass, and the run is not marked until every file is gone.</summary>
public sealed class ImportRetentionCleanup(
    IImportRunRepository runs, IImportFileRepository files, IFileStorageService storage, IOptions<ImportOptions> options, ILogger<ImportRetentionCleanup> logger)
{
    private const int Batch = 50;

    /// <returns>How many runs had their files deleted.</returns>
    public async Task<int> RunAsync(DateTime nowUtc, CancellationToken ct)
    {
        var done = 0;
        var cutoff = nowUtc.AddDays(-options.Value.EffectiveRetentionDays);
        foreach (var expired in await runs.ListWithExpiredFilesAsync(cutoff, Batch, ct))
        {
            if (ct.IsCancellationRequested) break;
            var paths = expired.TargetPaths.Append(expired.FeedbackFileUrl).Where(p => !string.IsNullOrEmpty(p)).Distinct().Select(p => p!).ToList();
            var allGone = true;
            foreach (var path in paths) allGone &= await TryDeleteAsync(path, ct);

            // The file that was uploaded for the run is named in its snapshot.
            if (ImportJson.Deserialize<ImportRunSnapshot>(expired.DefinitionSnapshotJson)?.File is { } file)
            {
                if (await TryDeleteAsync(file.StoragePath, ct)) await files.DeleteAsync(file.FileId, ct);
                else allGone = false;
            }
            if (!allGone) continue;
            await runs.MarkFilesExpiredAsync(expired.RunId, ct);
            done++;
        }

        // Uploaded files a run used whose retention ended, that no run still points at (the run's own clean-up above normally gets there first).
        foreach (var file in await files.ListRetentionEndedAsync(nowUtc, Batch, ct))
            if (await TryDeleteAsync(file.StoragePath, ct)) await files.DeleteAsync(file.PublicId, ct);
        return done;
    }

    private async Task<bool> TryDeleteAsync(string path, CancellationToken ct)
    {
        try
        {
            await storage.DeleteAsync(path, ct);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; } // already gone
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not delete an import file at the end of its retention; it will be tried again.");
            return false;
        }
    }
}
