using System;
using System.Collections.Generic;
using System.Linq;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;

namespace PowerBase.Application.Search;

/// <summary>Settings of the outbox relay (section "Search:Outbox"). Defaults are used for anything not configured.</summary>
public sealed class SearchOutboxOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>How often the relay looks for changes that were not confirmed.</summary>
    public int RelayIntervalSeconds { get; set; } = 30;
    /// <summary>A change younger than this is left to the normal path (the message published right after the write).</summary>
    public int GraceSeconds { get; set; } = 120;
    /// <summary>Minimum time between two re-publishes of the same change.</summary>
    public int RetryAfterSeconds { get; set; } = 300;
    /// <summary>After this many re-publishes a change is parked until the daily sweep re-drives it.</summary>
    public int MaxAttempts { get; set; } = 10;
    public int BatchSize { get; set; } = 500;
    public int TenantConcurrency { get; set; } = 4;
    /// <summary>Hour (UTC) of the daily check of what is still unconfirmed.</summary>
    public int DailySweepHourUtc { get; set; } = 2;
    /// <summary>A pending change older than this makes the health endpoint report Degraded.</summary>
    public int StaleAfterMinutes { get; set; } = 30;
}

public static class SearchOutboxPlanner
{
    /// <summary>
    /// The messages that put unconfirmed changes right: one per record. A delete wins over anything before it. Otherwise a single upsert
    /// with the newest version. The message carries no values: the indexer reads the record's current state from the database, so a
    /// republished change can never write old values over newer ones.
    /// </summary>
    public static IReadOnlyList<SearchIndexMessage> ToMessages(long tenantId, IEnumerable<SearchOutboxEntry> entries) =>
        entries
            .GroupBy(e => (e.TableId, e.RecordPublicId))
            .Select(g =>
            {
                var deletes = g.Where(e => e.Action == IndexAction.Delete).ToList();
                var chosen = deletes.Count > 0 ? deletes : g.ToList();
                var latest = chosen.MaxBy(e => e.Version)!;
                return new SearchIndexMessage
                {
                    Action = deletes.Count > 0 ? IndexAction.Delete : IndexAction.Upsert,
                    TenantId = tenantId,
                    AppId = latest.AppId,
                    TableId = g.Key.TableId,
                    RecordPublicId = g.Key.RecordPublicId,
                    Version = latest.Version,
                    Payload = null
                };
            })
            .ToList();

    /// <summary>Next occurrence of <paramref name="hourUtc"/>:00 UTC strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTime NextSweepUtc(DateTime nowUtc, int hourUtc)
    {
        var hour = Math.Clamp(hourUtc, 0, 23);
        var today = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, hour, 0, 0, DateTimeKind.Utc);
        return today > nowUtc ? today : today.AddDays(1);
    }
}
