using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

public sealed class StartImportRunHandler(
    ImportPlanBuilder planBuilder, IAppTableRepository tables, IAppRepository apps, IImportDefinitionRepository definitions,
    IImportRunRepository runs, IImportQueue queue, IQueryContext user, IImportDefinitionChecker checker,
    IImportFileRepository files, IImportFileAccess fileAccess)
{
    /// <summary>Validates the definition for the current user, snapshots it, and queues a run. Returns immediately —
    /// the work happens on the background worker. <paramref name="notifyOverride"/>, when given, replaces the people saved on the
    /// import for this run only (the person who starts it is always told). <paramref name="frontendBaseUrl"/> is the trusted
    /// address the completion email links to.</summary>
    public async Task<Guid> HandleAsync(
        Guid definitionId, string triggeredBy, IReadOnlyList<string>? notifyOverride, string? frontendBaseUrl, CancellationToken ct) =>
        (await StartAsync(definitionId, triggeredBy, notifyOverride, frontendBaseUrl, null, null, null, ct)).RunId;

    /// <summary>Starts a run for the run API. <paramref name="appId"/> must be the app the import belongs to. A
    /// <paramref name="clientToken"/> makes the call repeatable: the same token for the same import returns the run it first started
    /// (<c>Replayed</c>) instead of starting another, even if that run has finished.</summary>
    public async Task<ImportRunStart> StartAsync(
        Guid definitionId, string triggeredBy, IReadOnlyList<string>? notifyOverride, string? frontendBaseUrl,
        Guid? appId, string? clientToken, Guid? fileId, CancellationToken ct)
    {
        var def = await definitions.GetByPublicIdAsync(definitionId, ct) ?? throw new NotFoundException("ImportDefinition", definitionId);
        var destination = await tables.GetByIdAsync(def.DestinationTableId, ct);
        if (appId is { } expectedApp && await apps.GetIdByPublicIdAsync(expectedApp, ct) != destination.AppId)
            throw new NotFoundException("ImportDefinition", definitionId); // same answer as a missing import: do not reveal other apps' imports

        var key = ImportRunKey.From(clientToken);
        if (key is not null && await runs.FindPublicIdByKeyAsync(def.Id, key, ct) is { } earlier)
            return new ImportRunStart(earlier, true);
        // Re-check against the tables as they are now: a fixed import runs again, a newly broken one is stopped with the reason.
        await checker.RefreshAsync(def, ct);
        if (def.NeedsAttention)
            throw new ConflictException($"This import needs attention before it can run: {def.AttentionReason ?? "a mapped field changed"}.");

        var isFile = def.SourceKind == ImportSourceKinds.File;
        var source = isFile ? null : await tables.GetByIdAsync(def.SourceTableId ?? 0, ct);
        var config = ImportConfigMapper.ToConfig(def, source?.PublicId ?? Guid.Empty);
        if (notifyOverride is not null) config.NotifyEmails = ImportNotify.Normalize(notifyOverride);

        // A file import reads the file the person uploaded for this run: theirs, of the type the import expects, readable as set up.
        ImportRunFile? runFile = null;
        ImportFileSource? fileSource = null;
        if (isFile)
        {
            if (fileId is null) throw new ValidationException(new Dictionary<string, string[]> { ["File"] = ["Choose the file to import."] });
            var upload = await files.GetByPublicIdAsync(fileId.Value, ct);
            if (upload is null || upload.UploadedByUserId != user.UserId) throw new NotFoundException("ImportFile", fileId.Value);
            if (upload.Format != config.File!.Format)
                throw new ValidationException(new Dictionary<string, string[]> { ["File"] = [$"This import reads {(config.File.Format == ImportFileFormats.Xlsx ? "Excel (.xlsx)" : "CSV")} files, but the file you chose is {(upload.Format == ImportFileFormats.Xlsx ? "Excel" : "CSV")}."] });
            runFile = new ImportRunFile(upload.PublicId, upload.StoragePath, upload.FileName);
            try { fileSource = await fileAccess.OpenSourceAsync(new ImportRunSnapshot(destination.PublicId, config, null, runFile), countRows: false, ct); }
            catch (ImportFileFormatException ex) { throw new ValidationException(new Dictionary<string, string[]> { ["File"] = [ex.Message] }); }
        }
        else if (fileId is not null)
            throw new ValidationException(new Dictionary<string, string[]> { ["File"] = ["This import reads a table, not a file."] });

        // Fails fast with the real reason, for every table the import fills.
        foreach (var plan in await planBuilder.BuildAllAsync(config, destination.PublicId, ct, fileSource))
            await planBuilder.EnsureMergeKeyIsUniqueAsync(plan, ct);

        if (await runs.HasActiveRunAsync(def.Id, ct)) throw new ConflictException("This import is already running.");

        var run = new ImportRun
        {
            ImportDefinitionId = def.Id, TriggeredBy = triggeredBy, TriggeredByUserId = user.UserId, Status = ImportRunStatus.Queued, IdempotencyKey = key,
            DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(destination.PublicId, config, frontendBaseUrl, runFile))
        };
        if (await runs.CreateAsync(run, ct) == 0 && key is not null)
        {
            // A request with the same token got in between the check above and this insert: it is the run to return.
            var winner = await runs.FindPublicIdByKeyAsync(def.Id, key, ct) ?? throw new ConflictException("This import is already running.");
            return new ImportRunStart(winner, true);
        }
        await queue.EnqueueAsync(user.TenantId, run.PublicId, ct);
        return new ImportRunStart(run.PublicId, false);
    }
}

public sealed record ImportRunStart(Guid RunId, bool Replayed);

/// <summary>The stored form of a caller's client token: a digest, so the token itself is never kept, and bounded in length.</summary>
public static class ImportRunKey
{
    public const int MaxTokenLength = 200;

    public static string? From(string? clientToken)
    {
        var token = clientToken?.Trim();
        if (string.IsNullOrEmpty(token)) return null;
        if (token.Length > MaxTokenLength) throw new ValidationException(new Dictionary<string, string[]>
            { ["ClientToken"] = [$"The client token may be at most {MaxTokenLength} characters."] });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }
}

/// <summary>What a script calling the run API needs to know about a run: where it is and how it ended. No row data, so the
/// response stays small however large the import is; the details file is a separate download.</summary>
public sealed record ImportRunStatusResponse(
    Guid RunId, string Status, byte Progress, long RowsRead, long Inserted, long Updated, long Skipped, long Errored,
    DateTime? StartedOn, DateTime? CompletedOn, string? ErrorDetail, bool HasFeedback, long Unchanged = 0);

public sealed class GetImportRunStatusHandler(
    IAppAccessService access, IImportRunRepository runs, IAppTableRepository tables, IAppRepository apps, IQueryContext user)
{
    public async Task<ImportRunStatusResponse> HandleAsync(Guid appId, Guid runId, CancellationToken ct)
    {
        var run = await ImportRunAccess.LoadAsync(runId, runs, access, ct);
        // A run is only reachable through the app it belongs to; any other app answers as if the run did not exist.
        var snapshot = ImportJson.Deserialize<ImportRunSnapshot>(run.DefinitionSnapshotJson);
        var destination = snapshot is null ? null : await tables.GetByPublicIdAsync(snapshot.DestinationTableId, ct);
        if (destination is null || destination.AppId != await apps.GetIdByPublicIdAsync(appId, ct)) throw new NotFoundException("ImportRun", runId);
        return new ImportRunStatusResponse(run.PublicId, run.Status, run.Progress, run.RowsRead, run.Inserted, run.Updated, run.Skipped, run.Errored,
            run.StartedOn, run.CompletedOn, run.ErrorDetail, ImportRunAccess.CanSeeRows(run, user) && !string.IsNullOrEmpty(run.FeedbackFileUrl), run.Unchanged);
    }
}

public sealed class ListImportRunsHandler(
    IAppTableRepository tables, IAppAccessService access, IImportDefinitionRepository definitions, IImportRunRepository runs)
{
    public async Task<IReadOnlyList<ImportRunListItem>> HandleAsync(Guid definitionId, int take, CancellationToken ct)
    {
        var def = await definitions.GetByPublicIdAsync(definitionId, ct) ?? throw new NotFoundException("ImportDefinition", definitionId);
        await access.RequireMembershipByTablePublicIdAsync((await tables.GetByIdAsync(def.DestinationTableId, ct)).PublicId, ct);
        return await runs.ListByDefinitionAsync(def.Id, Math.Clamp(take, 1, 100), ct);
    }
}

/// <summary>Loads a run and enforces access to its destination table. Shared by the detail and feedback handlers so
/// both apply the same rule.</summary>
internal static class ImportRunAccess
{
    public static async Task<ImportRun> LoadAsync(Guid runId, IImportRunRepository runs, IAppAccessService access, CancellationToken ct)
    {
        var run = await runs.GetByPublicIdAsync(runId, ct) ?? throw new NotFoundException("ImportRun", runId);
        var snapshot = ImportJson.Deserialize<ImportRunSnapshot>(run.DefinitionSnapshotJson);
        if (snapshot is not null) await access.RequireMembershipByTablePublicIdAsync(snapshot.DestinationTableId, ct);
        return run;
    }

    /// <summary>The rows a run rejected, and the file listing them, carry values from the source table, read with the access
    /// of whoever started the run. So only that person, or an admin, may see them; other members of the destination app
    /// see the run's status and counts.</summary>
    public static bool CanSeeRows(ImportRun run, IQueryContext user) =>
        user.UserId == run.TriggeredByUserId || user.IsSuperAdmin || user.IsTenantAdmin;
}

public sealed class GetImportRunHandler(IAppAccessService access, IImportRunRepository runs, IQueryContext user)
{
    public const int IssuePageSize = 100;

    public async Task<ImportRunDetail> HandleAsync(Guid runId, CancellationToken ct)
    {
        var run = await ImportRunAccess.LoadAsync(runId, runs, access, ct);
        var canSeeRows = ImportRunAccess.CanSeeRows(run, user);
        var issues = canSeeRows ? await runs.ListIssuesAsync(run.Id, IssuePageSize, ct) : [];
        // The issue table keeps at most a capped number of rows; the real total is the run's own counters.
        var issueTotal = (int)Math.Min(int.MaxValue, run.Skipped + run.Errored);
        var sourceFile = ImportJson.Deserialize<ImportRunSnapshot>(run.DefinitionSnapshotJson)?.File;
        return new ImportRunDetail(
            new ImportRunListItem(run.PublicId, run.TriggeredBy, run.Status, run.Progress, run.RowsRead, run.Inserted, run.Updated,
                run.Skipped, run.Errored, run.StartedOn, run.CompletedOn, run.Unchanged),
            run.ErrorDetail, canSeeRows && !string.IsNullOrEmpty(run.FeedbackFileUrl) && run.FilesExpiredOn is null, issues, issueTotal,
            await TargetsAsync(run, ct), sourceFile?.FileName, canSeeRows && sourceFile is not null && run.FilesExpiredOn is null,
            run.FilesExpiredOn is not null);
    }

    /// <summary>Each table's counts, for a run that filled several. A run into one table has none and costs no extra query.</summary>
    private async Task<IReadOnlyList<ImportRunTargetItem>?> TargetsAsync(ImportRun run, CancellationToken ct)
    {
        if (ImportJson.Deserialize<ImportRunSnapshot>(run.DefinitionSnapshotJson)?.Config.AdditionalTargets is not { Count: > 0 }) return null;
        return await runs.ListTargetsAsync(run.Id, ct);
    }
}

public sealed record ImportFeedbackFile(Stream Content, string FileName);

/// <summary>Streams a run's feedback file after the same access check as the run itself. The storage path is never
/// returned to the client, so a leaked run id alone does not expose the file.</summary>
public sealed class GetImportFeedbackHandler(IAppAccessService access, IImportRunRepository runs, IFileStorageService storage, IQueryContext user)
{
    /// <param name="tableId">For a run that filled several tables: whose details file. Without it, the first table's (the run's own file).</param>
    public async Task<ImportFeedbackFile> HandleAsync(Guid runId, CancellationToken ct, Guid? tableId = null)
    {
        var run = await ImportRunAccess.LoadAsync(runId, runs, access, ct);
        if (!ImportRunAccess.CanSeeRows(run, user)) throw new UnauthorizedActionException("download this import's details");
        var path = run.FeedbackFileUrl;
        var label = "";
        if (tableId is { } table && ImportJson.Deserialize<ImportRunSnapshot>(run.DefinitionSnapshotJson)?.Config.AdditionalTargets is { Count: > 0 })
        {
            var target = await runs.GetTargetDetailsAsync(run.Id, table, ct) ?? throw new NotFoundException("ImportFeedback", runId);
            path = target.Path;
            label = "-" + new string(target.TableName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        }
        if (string.IsNullOrEmpty(path) || run.FilesExpiredOn is not null) throw new NotFoundException("ImportFeedback", runId);
        // Reading is an optional capability of a storage provider (both shipped providers have it).
        if (storage is not IFileStorageReadService readable)
            throw new InvalidOperationException("The configured file storage cannot read files back.");
        Stream content;
        try { content = await readable.OpenReadAsync(path, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new NotFoundException("ImportFeedback", runId);
        }
        var stamp = (run.CompletedOn ?? run.CreatedOn).ToString("yyyyMMdd-HHmm");
        return new ImportFeedbackFile(content, $"import-details{label}-{stamp}.csv");
    }
}

/// <summary>Stops a run: at once when it has not started, or after the chunk it is on. Rows already imported stay (an import is
/// not undone). Only the person who started it, or an admin, may stop it.</summary>
public sealed class CancelImportRunHandler(IAppAccessService access, IImportRunRepository runs, IQueryContext user, IAuditRepository audit)
{
    public async Task<ImportCancelOutcome> HandleAsync(Guid runId, CancellationToken ct)
    {
        var run = await ImportRunAccess.LoadAsync(runId, runs, access, ct);
        if (!ImportRunAccess.CanSeeRows(run, user)) throw new UnauthorizedActionException("stop this import");
        var outcome = await runs.RequestCancelAsync(run.Id, ct);
        if (outcome == ImportCancelOutcome.NotActive) throw new ConflictException("This import has already finished.");
        await audit.LogActivityAsync("ImportCancelRequested", "ImportRun", run.PublicId.ToString(), ct: ct);
        return outcome;
    }
}

/// <summary>The current user's runs that are active, and those that finished since they last asked: one small query that lets the
/// app tell them a run finished wherever they are in it, and stop asking once nothing is active.</summary>
public sealed class ListMyImportRunsHandler(IImportRunRepository runs, IQueryContext user)
{
    private const int Take = 20;
    private static readonly TimeSpan MaxLookBack = TimeSpan.FromHours(1);

    public async Task<ImportRunNotices> HandleAsync(DateTime? completedSince, CancellationToken ct)
    {
        // A browser that was closed for a day should not announce yesterday's runs: look back an hour at most.
        var floor = DateTime.UtcNow - MaxLookBack;
        DateTime? since = completedSince is { } requested ? (requested > floor ? requested : floor) : null;
        var (serverTime, list) = await runs.ListForUserAsync(user.UserId, since, Take, ct);
        return new ImportRunNotices(serverTime, list);
    }
}
