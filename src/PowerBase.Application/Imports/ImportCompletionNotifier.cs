using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports;

/// <summary>Emails the person who started an import, and anyone the import lists, when a run finishes. The email holds counts
/// and a link, never row values: the extra recipients may not be allowed to see the data, and the link leads to a page that
/// checks who is asking. A mail problem is logged and never undoes or fails the import.</summary>
public sealed class ImportCompletionNotifier(
    IUserRepository users, IAppTableRepository tables, IAppRepository apps, IEmailService email, ILogger<ImportCompletionNotifier> logger,
    IImportRunRepository? runs = null)
    : IImportNotifier
{
    public async Task<string?> NotifyAsync(ImportRun run, ImportRunSnapshot snapshot, CancellationToken ct = default)
    {
        string? failure = null;
        try
        {
            var recipients = new List<string>();
            var initiator = await users.GetByIdAsync(run.TriggeredByUserId, ct);
            if (!string.IsNullOrWhiteSpace(initiator.Email)) recipients.Add(initiator.Email.Trim().ToLowerInvariant());
            foreach (var extra in snapshot.Config.NotifyEmails)
                if (!recipients.Contains(extra, StringComparer.OrdinalIgnoreCase)) recipients.Add(extra);
            if (recipients.Count == 0) return "No completion email was sent: there is no email address on the account that ran this import.";

            var targets = runs is not null && snapshot.Config.AdditionalTargets.Count > 0 ? await runs.ListTargetsAsync(run.Id, ct) : null;
            var (subject, body) = Compose(run, snapshot.Config.Name, await LinkAsync(run, snapshot, ct), targets);
            foreach (var to in recipients)
            {
                try { await email.SendEmailAsync(to, subject, body, isHtml: true, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not email the result of import run {RunId} to a recipient.", run.PublicId);
                    failure ??= Describe(ex);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the completion email for import run {RunId}.", run.PublicId);
            failure ??= Describe(ex);
        }
        return failure is null ? null : $"The completion email could not be sent: {failure}";
    }

    /// <summary>What went wrong, in a sentence a person can pass on: the mail server's own wording, without any technical trace.</summary>
    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var text = new string((inner.Message ?? ex.Message).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= 250 ? text : text[..250];
    }

    /// <summary>The page of this run, or null when no trusted address for the frontend is known.</summary>
    private async Task<string?> LinkAsync(ImportRun run, ImportRunSnapshot snapshot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(snapshot.FrontendBaseUrl)) return null;
        var table = await tables.GetByPublicIdAsync(snapshot.DestinationTableId, ct);
        var appId = await apps.GetPublicIdByIdAsync(table.AppId, ct);
        return $"{snapshot.FrontendBaseUrl.TrimEnd('/')}/app/{appId}/imports/runs/{run.PublicId}";
    }

    internal static (string Subject, string Body) Compose(ImportRun run, string name, string? link, IReadOnlyList<ImportRunTargetItem>? targets = null)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()); // a name cannot add mail headers
        var (headline, summary) = run.Status switch
        {
            ImportRunStatus.Success => ("finished", "completed successfully"),
            ImportRunStatus.Partial => ("finished with some rows not imported", "completed, but some rows were not imported"),
            ImportRunStatus.Cancelled => ("was cancelled", "was cancelled"),
            _ => ("failed", "failed")
        };
        var html = new StringBuilder();
        html.Append("<p>The import <strong>").Append(WebUtility.HtmlEncode(clean)).Append("</strong> ").Append(summary).Append(".</p>");
        html.Append("<table cellpadding=\"4\" style=\"border-collapse:collapse\">");
        Row(html, "Rows read", run.RowsRead.ToString("N0"));
        Row(html, "Imported", $"{run.Inserted + run.Updated:N0}" + (run.Updated > 0 ? $" ({run.Inserted:N0} added, {run.Updated:N0} updated)" : ""));
        if (run.Unchanged > 0) Row(html, "Unchanged", run.Unchanged.ToString("N0"));
        Row(html, "Skipped", run.Skipped.ToString("N0"));
        Row(html, "Errors", run.Errored.ToString("N0"));
        if (run.StartedOn is { } started && run.CompletedOn is { } completed)
            Row(html, "Took", Duration(completed - started));
        html.Append("</table>");
        if (targets is { Count: > 0 })
        {
            // Counts for each table of a multi-table import; the totals above add them up.
            html.Append("<p>By table:</p><table cellpadding=\"4\" style=\"border-collapse:collapse\">");
            foreach (var t in targets)
                Row(html, WebUtility.HtmlEncode(t.TableName), $"{t.Inserted + t.Updated:N0} imported" + (t.Unchanged > 0 ? $", {t.Unchanged:N0} unchanged" : "") + $", {t.Skipped:N0} skipped, {t.Errored:N0} errors");
            html.Append("</table>");
        }
        if (!string.IsNullOrWhiteSpace(run.ErrorDetail)) html.Append("<p>").Append(WebUtility.HtmlEncode(run.ErrorDetail)).Append("</p>");
        if (link is not null)
            html.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(link)).Append("\">Open the run</a> to see the rows that were not imported and download the details.</p>");
        else
            html.Append("<p>Open the run in PowerBase to see the rows that were not imported.</p>");
        html.Append("<p style=\"color:#666;font-size:12px\">Row values are not included in this email.</p>");
        return ($"Import \"{clean}\" {headline}", html.ToString());
    }

    private static void Row(StringBuilder html, string label, string value) =>
        html.Append("<tr><td>").Append(label).Append("</td><td><strong>").Append(WebUtility.HtmlEncode(value)).Append("</strong></td></tr>");

    private static string Duration(TimeSpan span) => span.TotalSeconds < 1 ? "under a second"
        : span.TotalMinutes < 1 ? $"{span.Seconds} s"
        : span.TotalHours < 1 ? $"{span.Minutes} min {span.Seconds} s"
        : $"{(int)span.TotalHours} h {span.Minutes} min";
}
