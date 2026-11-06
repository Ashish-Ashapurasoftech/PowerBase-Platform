using System.Collections.Concurrent;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;

namespace PowerBase.Application.Apps.Commands.CopyApp;

/// <summary>One File value of a copied record: the copy's Record ID# and the value (plain JSON, never ciphertext).</summary>
public sealed record AppCopyFileValue(long RecordId, string? Value);

/// <summary>A file the copy could not carry over: the source's file no longer exists.</summary>
public sealed record AppCopyMissingFile(long RecordId, string FileName);

/// <summary>A page of File values after <see cref="AppCopyFileCopier.CopyPageAsync"/>: the values pointing at the
/// copy's own files, the files that were missing, and how many files were copied.</summary>
public sealed record AppCopyFilePage(
    IReadOnlyList<AppCopyFileValue> Values, IReadOnlyList<AppCopyMissingFile> Missing, int FilesCopied);

/// <summary>Copy App with data, "including file attachments": every file of a File value, the current file and each older
/// revision, gets its own copy, so the copy and the original never share a file (deleting a revision in one leaves the
/// other's file alone). Quickbase keeps a copy independent of its source the same way. The new files' names start with
/// <see cref="FilePrefix"/>, so the files of a copy that failed can be found and deleted.</summary>
public sealed class AppCopyFileCopier
{
    public const int DEFAULT_PARALLEL_COPIES = 16;
    public const int COPY_ATTEMPTS = 3;

    private readonly IFileStorageService _storage;
    private readonly IFileStorageCopyService _copier;
    private readonly string _prefix;
    private readonly int _parallelCopies;
    private readonly Func<int, TimeSpan> _retryDelay;

    public AppCopyFileCopier(IFileStorageService storage, Guid newAppPublicId,
        int parallelCopies = DEFAULT_PARALLEL_COPIES, Func<int, TimeSpan>? retryDelay = null)
    {
        _storage = storage;
        _copier = storage as IFileStorageCopyService
                  ?? throw new InvalidOperationException("The configured file storage provider cannot copy files.");
        _prefix = FilePrefix(newAppPublicId);
        _parallelCopies = Math.Max(1, parallelCopies);
        _retryDelay = retryDelay ?? (attempt => TimeSpan.FromSeconds(2 * attempt));
    }

    /// <summary>The start of every file name the copy of <paramref name="newAppPublicId"/> gets. Other files never start
    /// like this: uploads are named by a GUID ("d" format) or "import-…".</summary>
    public static string FilePrefix(Guid newAppPublicId) => $"appcopy-{newAppPublicId:N}-";

    /// <summary>Copies every file the page's values point at (each distinct path once, several at a time) and returns the
    /// values with the new paths. A missing source file is left out of its value and reported; any other failure is
    /// retried, then thrown, so that no file goes missing without a warning.</summary>
    public async Task<AppCopyFilePage> CopyPageAsync(IReadOnlyList<AppCopyFileValue> values, CancellationToken ct = default)
    {
        var sourcePaths = values.SelectMany(v => FileReservationContract.AllPaths(v.Value))
            .Distinct(StringComparer.Ordinal).ToList();

        var newPaths = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(sourcePaths,
            new ParallelOptions { MaxDegreeOfParallelism = _parallelCopies, CancellationToken = ct },
            async (path, token) => newPaths[path] = await CopyFileAsync(path, token));

        var copiedValues = new List<AppCopyFileValue>(values.Count);
        var missing = new List<AppCopyMissingFile>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var replaced = FileReservationContract.ReplacePaths(value.Value,
                path => newPaths.TryGetValue(path, out var newPath) ? newPath : null);
            copiedValues.Add(value with { Value = replaced.Value });
            missing.AddRange(replaced.MissingFiles.Select(name => new AppCopyMissingFile(value.RecordId, name)));
            used.UnionWith(FileReservationContract.AllPaths(replaced.Value));
        }

        // Revisions copied for an attachment whose current file was missing: the attachment was left out, so its copies
        // are not used by anything.
        foreach (var unused in newPaths.Values.OfType<string>().Where(path => !used.Contains(path)))
            await _storage.DeleteAsync(unused, ct);

        return new AppCopyFilePage(copiedValues, missing, used.Count);
    }

    /// <summary>The new file's path, or null when the source file does not exist.</summary>
    private async Task<string?> CopyFileAsync(string sourcePath, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // A new name on every attempt: a failed attempt may have left a partial file behind (deleted with the copy's
            // files if the copy fails; otherwise unused and harmless).
            var newName = _prefix + Guid.NewGuid().ToString("N") + Extension(sourcePath);
            try
            {
                return await _copier.CopyAsync(sourcePath, newName, ct);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (attempt < COPY_ATTEMPTS && ex is not OperationCanceledException)
            {
                await Task.Delay(_retryDelay(attempt), ct);
            }
        }
    }

    /// <summary>".pdf" of "/files/x.pdf" or "https://…/container/x.pdf".</summary>
    private static string Extension(string path)
        => Path.GetExtension(Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsolutePath : path);
}
