using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.ConnectedTables.Sync;

/// <summary>Emails about refresh problems: to the destination app's manager and (unless they opted out) the connection owner.
/// A failed email never fails the refresh.</summary>
public sealed class ConnectedNotifier(IEmailService email, IUserRepository users, IAppRepository apps, ILogger<ConnectedNotifier> logger)
{
    public Task NotifyFailureAsync(ConnectedTable table, string tableName, string reason, bool scheduleDisabled, CancellationToken ct)
    {
        var subject = scheduleDisabled
            ? $"Connected table \"{tableName}\" was switched to manual refresh"
            : $"Connected table \"{tableName}\" could not be refreshed";
        var body = $"""
            The connected table "{tableName}" could not be refreshed.

            Reason: {reason}
            {(scheduleDisabled ? "\nAfter several failed refreshes in a row its schedule was switched to Manual only. Fix the problem, then turn the schedule back on in the table's connection settings.\n" : "")}
            Check the table's refresh history for details.
            """;
        return SendAsync(table, subject, body, ct);
    }

    public Task NotifyHeldAsync(ConnectedTable table, string tableName, long toDelete, long live, CancellationToken ct)
    {
        var subject = $"Connected table \"{tableName}\" needs your confirmation";
        var body = $"""
            A refresh of the connected table "{tableName}" would remove {toDelete:N0} of its {live:N0} records, because they are no longer in the source.
            Nothing has been removed yet.

            If this is expected, confirm the deletion in the table's connection settings. If not, check the connection's permissions and the table's filter.
            """;
        return SendAsync(table, subject, body, ct);
    }

    private async Task SendAsync(ConnectedTable table, string subject, string body, CancellationToken ct)
    {
        try
        {
            var ids = new HashSet<long>();
            if (table.NotifyOnFailure) ids.Add(table.OwnerUserId);
            try { ids.Add((await apps.GetByIdAsync(table.AppId, ct)).OwnerId); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "No app manager for connected table {Id}.", table.PublicId); }
            if (ids.Count == 0) return;

            var identities = await users.GetIdentitiesByIdsAsync(ids, ct);
            foreach (var to in identities.Values.Select(u => u.Email).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase))
                await email.SendEmailAsync(to, subject, body, isHtml: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the connected-table notification for {Id}.", table.PublicId);
        }
    }
}
