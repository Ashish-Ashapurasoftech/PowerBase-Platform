using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Mapping;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Import.Jobs;

/// <summary>Files larger than this are imported in the background instead of while the user waits.</summary>
public static class AppImportJobPolicy
{
    public const long BackgroundThresholdBytes = 3L * 1024 * 1024;
    public static bool RunsInBackground(long fileSizeBytes) => fileSizeBytes > BackgroundThresholdBytes;
}

/// <summary>Lets the import report how far along it is without every handler needing to know whether anyone is listening:
/// a background job sets <see cref="Current"/> for the duration of its run; synchronous imports leave it null and the
/// calls do nothing.</summary>
public static class ImportProgress
{
    private static readonly AsyncLocal<Action<string, int>?> _current = new();

    public static Action<string, int>? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public static void Report(string stage, int percent) => _current.Value?.Invoke(stage, Math.Clamp(percent, 0, 99));
}

/// <summary>Who started a background import and what they were allowed to do — restored on the worker so the import runs with
/// exactly the caller's rights, no more.</summary>
public sealed record AppImportJobRequest(
    Guid JobPublicId, long TenantId, long UserId, bool IsSuperAdmin, string UserName, string UserEmail,
    IReadOnlySet<string> Permissions, string TenantRole);

/// <summary>In-process queue between the upload endpoint and <c>AppImportJobWorker</c>.</summary>
public sealed class AppImportJobQueue
{
    private readonly Channel<AppImportJobRequest> _channel = Channel.CreateUnbounded<AppImportJobRequest>();

    /// <summary>When this server process started; jobs older than this that never finished were lost with the previous process.</summary>
    public DateTime ProcessStartedUtc { get; } = DateTime.UtcNow;

    public ValueTask EnqueueAsync(AppImportJobRequest request, CancellationToken ct = default) => _channel.Writer.WriteAsync(request, ct);
    public IAsyncEnumerable<AppImportJobRequest> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed record AppImportJobDto(
    Guid Id, string FileName, long FileSizeBytes, string Status, string? Stage, int PercentComplete,
    Guid? AppId, string? AppName, string? ErrorMessage, DateTime CreatedOn, DateTime? StartedOn, DateTime? CompletedOn, bool EmailSent);

/// <summary>Starts, lists and dismisses the caller's background imports.</summary>
public sealed class AppImportJobService(IAppImportJobRepository jobs, IQueryContext context, AppImportJobQueue queue)
{
    public async Task<AppImportJobDto> StartAsync(string fileName, byte[] content, string? mapping, string? frontendBaseUrl, CancellationToken ct)
    {
        var name = new string(Path.GetFileName(fileName ?? "import").Where(c => !char.IsControl(c)).ToArray());
        var job = await jobs.CreateAsync(new AppImportJob
        {
            UserId = context.UserId,
            FileName = name.Length == 0 ? "import" : (name.Length > 260 ? name[..260] : name),
            FileSizeBytes = content.LongLength,
            Status = AppImportJobStatus.Queued,
            Stage = "Queued",
            Content = content,
            MappingJson = mapping,
            FrontendBaseUrl = frontendBaseUrl,
        }, ct);

        await queue.EnqueueAsync(new AppImportJobRequest(
            job.PublicId, context.TenantId, context.UserId, context.IsSuperAdmin, context.UserName, context.UserEmail,
            new HashSet<string>(context.Permissions), context.TenantRole), ct);
        return ToDto(job);
    }

    public async Task<IReadOnlyList<AppImportJobDto>> ListMineAsync(CancellationToken ct)
    {
        await jobs.FailInterruptedAsync(context.UserId, queue.ProcessStartedUtc, ct);
        return (await jobs.ListForUserAsync(context.UserId, ct)).Select(ToDto).ToList();
    }

    public async Task<AppImportJobDto> GetAsync(Guid id, CancellationToken ct)
    {
        await jobs.FailInterruptedAsync(context.UserId, queue.ProcessStartedUtc, ct);
        var job = await jobs.GetByPublicIdAsync(id, ct);
        // Another person's job looks exactly like one that does not exist.
        if (job is null || job.UserId != context.UserId) throw new NotFoundException("AppImportJob", id.ToString());
        return ToDto(job);
    }

    /// <summary>The full import report of a finished job (what the synchronous import returns).</summary>
    public async Task<JsonElement?> GetResultAsync(Guid id, CancellationToken ct)
    {
        var job = await jobs.GetByPublicIdAsync(id, ct);
        if (job is null || job.UserId != context.UserId) throw new NotFoundException("AppImportJob", id.ToString());
        if (job.ResultJson is null) return null;
        using var doc = JsonDocument.Parse(job.ResultJson);
        return doc.RootElement.Clone();
    }

    public async Task DismissAsync(Guid id, CancellationToken ct) => await jobs.DismissAsync(id, context.UserId, ct);

    internal static AppImportJobDto ToDto(AppImportJob j) => new(
        j.PublicId, j.FileName, j.FileSizeBytes, j.Status, j.Stage, j.PercentComplete, j.AppPublicId, j.AppName, j.ErrorMessage,
        j.CreatedOn, j.StartedOn, j.CompletedOn, j.EmailSentOn.HasValue);
}

/// <summary>Built-in email templates for finished background imports and the placeholders they accept. A tenant can override
/// either one (subject, HTML body, on/off); with no override the defaults below are used.</summary>
public static class AppImportEmailTemplates
{
    public const string Completed = "AppImportCompleted";
    public const string Failed = "AppImportFailed";

    public static readonly IReadOnlyList<string> Keys = [Completed, Failed];

    public static readonly IReadOnlyList<string> Placeholders =
    [
        "UserName", "FileName", "AppName", "Status", "Duration", "TablesCreated", "FieldsCreated", "RelationshipsCreated",
        "ReportsCreated", "FormsCreated", "RolesCreated", "SkippedCount", "ErrorMessage", "AppLink",
    ];

    public static (string Subject, string Body) Default(string key) => key switch
    {
        Completed => ("Your app import finished: {{AppName}}",
            "<p>Hi {{UserName}},</p>" +
            "<p>Your import of <strong>{{FileName}}</strong> has finished. The app <strong>{{AppName}}</strong> is ready.</p>" +
            "<table cellpadding=\"4\" style=\"border-collapse:collapse\">" +
            "<tr><td>Tables</td><td><strong>{{TablesCreated}}</strong></td></tr>" +
            "<tr><td>Fields</td><td><strong>{{FieldsCreated}}</strong></td></tr>" +
            "<tr><td>Relationships</td><td><strong>{{RelationshipsCreated}}</strong></td></tr>" +
            "<tr><td>Reports</td><td><strong>{{ReportsCreated}}</strong></td></tr>" +
            "<tr><td>Forms</td><td><strong>{{FormsCreated}}</strong></td></tr>" +
            "<tr><td>Items not created</td><td><strong>{{SkippedCount}}</strong></td></tr>" +
            "<tr><td>Took</td><td><strong>{{Duration}}</strong></td></tr></table>" +
            "<p><a href=\"{{AppLink}}\">Open the app</a></p>"),
        Failed => ("Your app import failed: {{FileName}}",
            "<p>Hi {{UserName}},</p>" +
            "<p>Your import of <strong>{{FileName}}</strong> could not be completed.</p>" +
            "<p>{{ErrorMessage}}</p><p>Nothing was left behind. You can fix the file and upload it again.</p>"),
        _ => throw new NotFoundException("EmailTemplate", key),
    };

    private static readonly Regex Token = new(@"\{\{\s*(\w+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Replaces <c>{{Name}}</c> tokens. Values are HTML-encoded (they come from files and names a user typed); unknown
    /// tokens are removed rather than shown.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values, bool encode) =>
        Token.Replace(template, m =>
        {
            var v = values.TryGetValue(m.Groups[1].Value, out var found) ? found : "";
            return encode ? WebUtility.HtmlEncode(v) : new string(v.Where(c => !char.IsControl(c)).ToArray());
        });
}

/// <summary>Runs one queued background import: restores progress reporting, executes the normal import handler, stores the result
/// and emails the person who started it. A mail problem is recorded on the job and never changes the import's outcome.</summary>
public sealed class AppImportJobRunner(
    IAppImportJobRepository jobs, ImportAppFromPblCommandHandler importHandler, IUserRepository users, IEmailService email,
    IEmailTemplateRepository templates, ILogger<AppImportJobRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(Guid jobPublicId, CancellationToken ct)
    {
        var job = await jobs.GetWithContentAsync(jobPublicId, ct);
        if (job is null) return;

        await jobs.MarkRunningAsync(job.Id, ct);
        var lastWrite = DateTime.MinValue;
        var lastPercent = -1;
        ImportProgress.Current = (stage, percent) =>
        {
            // Skip no-change and bursts; progress is advisory, so a lost update is harmless.
            if (percent == lastPercent || (DateTime.UtcNow - lastWrite).TotalMilliseconds < 400) return;
            lastWrite = DateTime.UtcNow;
            lastPercent = percent;
            try { jobs.UpdateProgressAsync(job.Id, stage, percent, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not store import progress for job {Job}.", job.PublicId); }
        };

        ImportReport? report = null;
        string? error = null;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(job.Content ?? []);
            report = await importHandler.HandleAsync(new ImportAppFromPblCommand(text, ImportMode.CreateNewApp, ImportMapping.Parse(job.MappingJson)), ct);
            await jobs.CompleteAsync(job.Id, report.AppPublicId, report.AppName, JsonSerializer.Serialize(report, Json), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Background import job {Job} failed.", job.PublicId);
            error = Describe(ex);
            await jobs.FailAsync(job.Id, error, CancellationToken.None);
        }
        finally
        {
            ImportProgress.Current = null;
        }

        await NotifyAsync(job, report, error, ct);
    }

    private async Task NotifyAsync(AppImportJob job, ImportReport? report, string? error, CancellationToken ct)
    {
        try
        {
            var user = await users.GetByIdAsync(job.UserId, ct);
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                await jobs.SetEmailResultAsync(job.Id, false, "The account has no email address.", CancellationToken.None);
                return;
            }

            var key = report is null ? AppImportEmailTemplates.Failed : AppImportEmailTemplates.Completed;
            var custom = await templates.GetAsync(key, ct);
            if (custom is { IsEnabled: false })
            {
                await jobs.SetEmailResultAsync(job.Id, false, "Email is switched off for this template.", CancellationToken.None);
                return;
            }
            var (subject, body) = custom is null ? AppImportEmailTemplates.Default(key) : (custom.Subject, custom.Body);

            var took = job.StartedOn.HasValue ? DateTime.UtcNow - job.StartedOn.Value : TimeSpan.Zero;
            var link = report is not null && !string.IsNullOrWhiteSpace(job.FrontendBaseUrl)
                ? $"{job.FrontendBaseUrl!.TrimEnd('/')}/app/{report.AppPublicId}"
                : "";
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["UserName"] = string.IsNullOrWhiteSpace(user.Name) ? "there" : user.Name,
                ["FileName"] = job.FileName,
                ["AppName"] = report?.AppName ?? "",
                ["Status"] = report is null ? "Failed" : "Completed",
                ["Duration"] = took.TotalMinutes < 1 ? $"{Math.Max(1, (int)took.TotalSeconds)} s" : $"{(int)took.TotalMinutes} min {took.Seconds} s",
                ["TablesCreated"] = (report?.TablesCreated ?? 0).ToString(),
                ["FieldsCreated"] = (report?.FieldsCreated ?? 0).ToString(),
                ["RelationshipsCreated"] = (report?.RelationshipsCreated ?? 0).ToString(),
                ["ReportsCreated"] = (report?.ReportsCreated ?? 0).ToString(),
                ["FormsCreated"] = (report?.FormsCreated ?? 0).ToString(),
                ["RolesCreated"] = (report?.RolesCreated ?? 0).ToString(),
                ["SkippedCount"] = (report?.Skipped.Count ?? 0).ToString(),
                ["ErrorMessage"] = error ?? "",
                ["AppLink"] = link,
            };

            await email.SendEmailAsync(user.Email.Trim(),
                AppImportEmailTemplates.Render(subject, values, encode: false),
                AppImportEmailTemplates.Render(body, values, encode: true), isHtml: true, ct);
            await jobs.SetEmailResultAsync(job.Id, true, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not email the result of import job {Job}.", job.PublicId);
            await jobs.SetEmailResultAsync(job.Id, false, ex.Message, CancellationToken.None);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        ValidationException ve => string.Join(" ", ve.Errors.SelectMany(e => e.Value)),
        DomainException de => de.Message,
        _ => "The import could not be completed because of an unexpected error.",
    };
}
