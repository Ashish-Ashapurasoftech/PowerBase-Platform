using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Caching;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Services;

/// <summary>Settings of the report result cache ("ResultCache"). Off unless Enabled.</summary>
public sealed class ResultCacheOptions
{
    public const string SectionName = "ResultCache";
    public bool Enabled { get; set; }
    /// <summary>How long an answer is kept; never more than 60 s (doc 23 §5). Bounds what no version can see: Today(), user renames.</summary>
    public int TtlSeconds { get; set; } = 60;
    /// <summary>Larger answers are not stored (a summary with thousands of groups is cheap to send again, costly to keep).</summary>
    public int MaxBodyBytes { get; set; } = 1_000_000;
}

/// <summary>
/// See <see cref="IReportResultCache"/>. The data version is the tenant database's rowversion counter (<c>@@DBTS</c>): every data table
/// and most meta tables carry a ROWVERSION, so ANY insert, update or soft delete there — from the API, imports, delete jobs, pipelines
/// or the compute worker — moves it, with no bump to remember in each write path. Commit does not move it, so an answer is stored only
/// when <c>MIN_ACTIVE_ROWVERSION() = @@DBTS + 1</c> (no write open as the question was read). The rest — field definitions,
/// relationships, role rules, user permissions — is covered by the metadata versions. Keys hold ids and hashes only.
/// </summary>
public sealed class ReportResultCache : TenantRepositoryBase, IReportResultCache
{
    private const string DataVersionSql = """
        SELECT CONVERT(BIGINT, @@DBTS) AS Dbts,
               CASE WHEN CONVERT(BIGINT, MIN_ACTIVE_ROWVERSION()) = CONVERT(BIGINT, @@DBTS) + 1 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS Quiet
        """;

    private static readonly ConcurrentDictionary<Guid, long> NotCacheableUntil = new();
    private static long _downUntilTicks;
    private static readonly TimeSpan NotCacheableFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IDistributedCache? _l2;
    private readonly IMetadataCache _metadata;
    private readonly ResultCacheOptions _options;
    private readonly bool _replicasOn;
    private readonly ILogger<ReportResultCache> _logger;

    public ReportResultCache(ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IMetadataCache metadata,
        ResultCacheOptions options, IConfiguration configuration, ILogger<ReportResultCache> logger, IDistributedCache? l2 = null)
        : base(connectionFactory, queryContext)
    {
        _metadata = metadata;
        _options = options;
        _l2 = l2;
        _logger = logger;
        // A summary may be read from a replica that is behind the primary's counter: the answer could be older than its key.
        _replicasOn = configuration.GetValue("ReadReplica:Enabled", false);
    }

    private bool On => _options.Enabled && _l2 is not null && !_replicasOn;

    public async Task<ReportResultLookup> LookupAsync(Guid reportPublicId, string question, CancellationToken ct = default)
    {
        // No tenant or user yet (an unauthenticated call the authorization filters will refuse): nothing to look up.
        if (!On || QueryContext.TenantId <= 0 || QueryContext.UserId <= 0) return ReportResultLookup.None;
        var now = DateTime.UtcNow.Ticks;
        if (NotCacheableUntil.TryGetValue(reportPublicId, out var until) && now < until) return ReportResultLookup.None;
        if (now < Interlocked.Read(ref _downUntilTicks)) { CacheMetrics.ResultCache("skip-down"); return ReportResultLookup.None; }

        var stamp = await _metadata.VersionStampAsync(QueryContext.TenantId);
        if (stamp is null) { CacheMetrics.ResultCache("skip-versions"); return ReportResultLookup.None; }

        (long Dbts, bool Quiet) version;
        await using (var connection = await OpenConnectionAsync(ct))
            version = await connection.QuerySingleAsync<(long, bool)>(new CommandDefinition(DataVersionSql, cancellationToken: ct));
        if (!version.Quiet) { CacheMetrics.ResultCache("skip-write-open"); return ReportResultLookup.None; }

        var key = KeyFor(QueryContext.TenantId, QueryContext.UserId, stamp, version.Dbts, reportPublicId, question);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReadTimeout);
            var body = await _l2!.GetAsync(key, timeout.Token);
            CacheMetrics.ResultCache(body is { Length: > 0 } ? "hit" : "miss");
            return new ReportResultLookup(key, body is { Length: > 0 } ? body : null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Down(ex);
            return ReportResultLookup.None;
        }
    }

    public void Store(ReportResultLookup lookup, byte[] body)
    {
        if (lookup.Key is null || !On || body.Length > _options.MaxBodyBytes) return;
        var key = lookup.Key;
        var ttl = TimeSpan.FromSeconds(Math.Clamp(_options.TtlSeconds, 1, 60));
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _l2!.SetAsync(key, body, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, timeout.Token);
                CacheMetrics.ResultCacheBytes(body.Length);
            }
            catch (Exception ex) { Down(ex); }
        });
    }

    public void MarkNotCacheable(Guid reportPublicId)
    {
        if (!On) return;
        if (NotCacheableUntil.Count > 50_000) NotCacheableUntil.Clear();
        NotCacheableUntil[reportPublicId] = (DateTime.UtcNow + NotCacheableFor).Ticks;
    }

    /// <summary>The only place a result key is built: tenant first, then user, the hashed versions and question (doc 23 §4).</summary>
    internal static string KeyFor(long tenantId, long userId, string stamp, long dbts, Guid reportPublicId, string question)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(stamp, "\u0001", question))));
        return $"rr:t{tenantId}:u{userId}:d{dbts}:r{reportPublicId:N}:{hash}";
    }

    private void Down(Exception ex)
    {
        Interlocked.Exchange(ref _downUntilTicks, (DateTime.UtcNow + TimeSpan.FromSeconds(30)).Ticks);
        CacheMetrics.ResultCache("error");
        _logger.LogWarning(ex, "Report result cache (Redis) unavailable; reports run without it for 30 s");
    }

    internal static void ResetForTests()
    {
        NotCacheableUntil.Clear();
        Interlocked.Exchange(ref _downUntilTicks, 0);
    }
}
