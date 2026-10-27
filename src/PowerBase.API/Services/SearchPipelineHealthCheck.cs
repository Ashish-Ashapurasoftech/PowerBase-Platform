using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Search;
using PowerBase.Infrastructure.Services;

namespace PowerBase.API.Services;

/// <summary>
/// Health of the record-to-search pipeline, from this process's point of view:
/// Unhealthy - the search service is configured but not answering, or an indexer worker has not looked at its queue for minutes (stopped);
/// Degraded  - changes are waiting in the outbox longer than expected, or rows gave up and wait for the daily check;
/// Healthy   - otherwise. The report holds counts and times only.
/// </summary>
public class SearchPipelineHealthCheck : IHealthCheck
{
    private static readonly TimeSpan WorkerSilence = TimeSpan.FromMinutes(5);

    private readonly SearchPipelineState _state;
    private readonly IAzureSearchService _search;
    private readonly SearchOutboxOptions _outbox;
    private readonly bool _indexerEnabled;

    public SearchPipelineHealthCheck(SearchPipelineState state, IAzureSearchService search, IConfiguration configuration)
    {
        _state = state;
        _search = search;
        _outbox = configuration.GetSection("Search:Outbox").Get<SearchOutboxOptions>() ?? new SearchOutboxOptions();
        _indexerEnabled = configuration.GetValue("Search:Indexer:Enabled", true);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        var problems = new List<string>();
        var warnings = new List<string>();
        var now = DateTime.UtcNow;

        data["searchConfigured"] = _search.IsEnabled;
        if (_search.IsEnabled)
        {
            var healthy = await _search.IsHealthyAsync(cancellationToken);
            data["searchReachable"] = healthy;
            if (!healthy) problems.Add("The search service is not answering.");
        }

        if (_indexerEnabled && _search.IsEnabled)
        {
            foreach (var (worker, last) in _state.WorkerLoops)
            {
                var silent = now - last;
                data[$"worker:{worker}:lastLoopSecondsAgo"] = (int)silent.TotalSeconds;
                if (silent > WorkerSilence) problems.Add($"{worker} has not looked at its queue for {(int)silent.TotalMinutes} minutes.");
            }
            if (_state.WorkerLoops.Count == 0) warnings.Add("No indexer worker has started yet.");
        }

        data["indexedDocuments"] = _state.Indexed;
        data["rejectedDocuments"] = _state.Rejected;
        data["deadLettered"] = _state.DeadLettered;
        data["outboxPending"] = _state.OutboxPending;
        data["outboxParked"] = _state.OutboxParked;
        if (_state.OutboxOldestPendingUtc is { } oldest)
        {
            var age = now - oldest;
            data["outboxOldestMinutes"] = (int)age.TotalMinutes;
            if (age > TimeSpan.FromMinutes(_outbox.StaleAfterMinutes))
                warnings.Add($"{_state.OutboxPending} change(s) have been waiting for the index, the oldest for {(int)age.TotalMinutes} minutes.");
        }
        if (_state.OutboxParked > 0) warnings.Add($"{_state.OutboxParked} change(s) gave up and wait for the daily check.");

        if (problems.Count > 0) return HealthCheckResult.Unhealthy(string.Join(" ", problems.Concat(warnings)), data: data);
        if (warnings.Count > 0) return HealthCheckResult.Degraded(string.Join(" ", warnings), data: data);
        return HealthCheckResult.Healthy("The search pipeline is running.", data);
    }

    public static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var entry = report.Entries.Values.FirstOrDefault();
        var body = new
        {
            status = report.Status.ToString(),
            description = entry.Description,
            details = entry.Data
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
