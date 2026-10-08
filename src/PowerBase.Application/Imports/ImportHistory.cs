using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

/// <summary>What the history screen asks for. A run that filled several tables is one row per table.</summary>
public sealed record ImportHistoryQuery(
    long AppId, DateTime From, DateTime? To, Guid? ImportId, Guid? TableId, string? Status, string? Trigger, int Skip, int Take);

/// <summary>One row of the history as the database gives it: one table of one run.</summary>
public sealed record ImportHistoryRow(
    Guid RunId, Guid ImportId, string ImportName, Guid TableId, string TableName, int TableCount, string SourceKind, string? SourceFileName,
    string Status, string Trigger, long TriggeredByUserId, DateTime CreatedOn, DateTime? StartedOn, DateTime? CompletedOn,
    long RowsRead, long Inserted, long Updated, long Unchanged, long Skipped, long Errored, string? ErrorDetail, bool HasDetails,
    DateTime? FilesExpiredOn, int Total);

/// <summary>One row of the history as the screen shows it.</summary>
/// <param name="TableCount">How many tables the run filled (the row is this one of them).</param>
/// <param name="HasDetails">There is a details file for this table that can still be downloaded by this person.</param>
/// <param name="HasSourceFile">The file that was uploaded for the run can still be downloaded by this person.</param>
/// <param name="FilesExpired">The retention ended and the run's files were deleted (or are about to be); the counts stay.</param>
/// <param name="FilesExpireOn">When the files go (or went); null while the run is still going.</param>
public sealed record ImportHistoryItem(
    Guid RunId, Guid ImportId, string ImportName, Guid TableId, string TableName, int TableCount, string SourceKind, string? SourceFileName,
    string Status, string Trigger, string? StartedBy, DateTime CreatedOn, DateTime? StartedOn, DateTime? CompletedOn, double? DurationSeconds,
    long RowsRead, long Inserted, long Updated, long Unchanged, long Skipped, long Errored, string? Memo,
    bool HasDetails, bool HasSourceFile, bool FilesExpired, DateTime? FilesExpireOn);

/// <param name="RetentionDays">How long a run's files are kept, so the screen can say when they go.</param>
public sealed record ImportHistoryPage(int Total, int Page, int PageSize, DateTime From, IReadOnlyList<ImportHistoryItem> Items, int RetentionDays = 30);

/// <summary>The history of an app's imports: every run in a period (the last 30 days unless asked otherwise), newest first, with what each did to
/// each table. Any member of the app sees status and counts; the files carry source values, so, as for a run's own page, only the person who
/// started the run or an admin can download them.</summary>
public sealed class ListImportHistoryHandler(
    IAppRepository apps, IAppAccessService access, IImportRunRepository runs, IUserRepository users, IQueryContext user, IOptions<ImportOptions> options)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 400;
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> Statuses = [ImportRunStatus.Queued, ImportRunStatus.Running, ImportRunStatus.Success, ImportRunStatus.Partial, ImportRunStatus.Failed, ImportRunStatus.Cancelled];
    private static readonly HashSet<string> Triggers = [ImportTrigger.Manual, ImportTrigger.Schedule, ImportTrigger.Api];

    public async Task<ImportHistoryPage> HandleAsync(
        Guid appId, DateTime? from, DateTime? to, Guid? importId, Guid? tableId, string? status, string? trigger, int page, int pageSize, CancellationToken ct)
    {
        await access.RequireMembershipByAppPublicIdAsync(appId, ct);
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        trigger = string.IsNullOrWhiteSpace(trigger) ? null : trigger.Trim().ToLowerInvariant();
        if (status is not null && !Statuses.Contains(status)) throw Invalid("Status", "That is not a run status.");
        if (trigger is not null && !Triggers.Contains(trigger)) throw Invalid("Trigger", "That is not a way a run starts.");

        var now = DateTime.UtcNow;
        var start = (from ?? now.AddDays(-DefaultDays)).ToUniversalTime();
        if (start < now.AddDays(-MaxDays)) start = now.AddDays(-MaxDays);
        var end = to?.ToUniversalTime();
        if (end is { } e && e <= start) throw Invalid("To", "The end of the period must be after its start.");
        pageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        page = Math.Max(1, page);

        var rows = await runs.ListHistoryAsync(new ImportHistoryQuery(
            await apps.GetIdByPublicIdAsync(appId, ct), start, end, importId, tableId, status, trigger, (page - 1) * pageSize, pageSize), ct);
        var names = rows.Count == 0 ? new Dictionary<long, string>() : await users.GetNamesByIdsAsync(rows.Select(r => r.TriggeredByUserId).Distinct().ToList(), ct);
        var retention = options.Value.EffectiveRetentionDays;

        var items = rows.Select(r =>
        {
            var canDownload = user.UserId == r.TriggeredByUserId || user.IsSuperAdmin || user.IsTenantAdmin;
            var expireOn = r.CompletedOn?.AddDays(retention);
            // Expired once the clean-up has run, or once the time is up even if it has not got to it yet.
            var expired = r.FilesExpiredOn is not null || (expireOn is { } on && on <= now);
            return new ImportHistoryItem(
                r.RunId, r.ImportId, r.ImportName, r.TableId, r.TableName, r.TableCount, r.SourceKind, r.SourceFileName, r.Status, r.Trigger,
                names.GetValueOrDefault(r.TriggeredByUserId), r.CreatedOn, r.StartedOn, r.CompletedOn,
                r.StartedOn is { } s && r.CompletedOn is { } c ? Math.Round((c - s).TotalSeconds, 1) : null,
                r.RowsRead, r.Inserted, r.Updated, r.Unchanged, r.Skipped, r.Errored, r.ErrorDetail,
                canDownload && r.HasDetails && !expired, canDownload && r.SourceFileName is not null && !expired, expired && (r.HasDetails || r.SourceFileName is not null), expireOn);
        }).ToList();
        return new ImportHistoryPage(rows.Count == 0 ? 0 : rows[0].Total, page, pageSize, start, items, retention);
    }

    private static ValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>The file that was uploaded for a run, for the person who started it (or an admin), while it is still kept. The storage path is never
/// returned to the client.</summary>
public sealed class GetImportSourceFileHandler(IAppAccessService access, IImportRunRepository runs, IFileStorageService storage, IQueryContext user)
{
    public async Task<ImportFeedbackFile> HandleAsync(Guid runId, CancellationToken ct)
    {
        var run = await ImportRunAccess.LoadAsync(runId, runs, access, ct);
        if (!ImportRunAccess.CanSeeRows(run, user)) throw new UnauthorizedActionException("download the file this import read");
        var file = ImportJson.Deserialize<ImportRunSnapshot>(run.DefinitionSnapshotJson)?.File;
        // Past the retention the file is gone (or about to be): say it is not found rather than hand out a half-deleted file.
        if (file is null || run.FilesExpiredOn is not null) throw new NotFoundException("ImportSourceFile", runId);
        if (storage is not IFileStorageReadService readable) throw new InvalidOperationException("The configured file storage cannot read files back.");
        try { return new ImportFeedbackFile(await readable.OpenReadAsync(file.StoragePath, ct), file.FileName); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw new NotFoundException("ImportSourceFile", runId); }
    }
}
