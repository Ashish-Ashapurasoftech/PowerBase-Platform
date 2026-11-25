using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Persistence;

public class TenantConnectionFactory : ITenantConnectionFactory
{
    private readonly ITenantConnectionResolver _resolver;
    private readonly IQueryContext _queryContext;
    private string? _resolvedConnStr;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly IReplicaLagMonitor? _lagMonitor;
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;

    public TenantConnectionFactory(ITenantConnectionResolver resolver, IQueryContext queryContext,
        IReplicaLagMonitor? lagMonitor = null, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _resolver = resolver;
        _queryContext = queryContext;
        _lagMonitor = lagMonitor;
        _configuration = configuration;
    }

    private long _currentTenantId;

    /// <summary>
    /// Default command timeout (seconds) for every tenant connection of this process; null = the SqlClient default (30 s). Set only by
    /// background hosts (the compute worker, config Compute:CommandTimeoutSeconds), never by the API.
    /// </summary>
    public static int? ProcessCommandTimeoutSeconds { get; set; }

    public async Task<SqlConnection> CreateAsync(CancellationToken ct = default)
    {
        if (_queryContext.TenantId == 0)
            throw new InvalidOperationException("No tenant in scope. TenantId is 0.");

        if (_resolvedConnStr is null || _currentTenantId != _queryContext.TenantId)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_resolvedConnStr is null || _currentTenantId != _queryContext.TenantId)
                {
                    _resolvedConnStr = await _resolver.ResolveAsync(_queryContext.TenantId, ct);
                    _currentTenantId = _queryContext.TenantId;
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        // An import (hundreds of CREATE/ALTERs) runs with a longer command timeout than a normal request; so does a background process that
        // sets ProcessCommandTimeoutSeconds (the compute worker: the read code's summaries over a million child rows exceed the 30 s default).
        if ((PowerBase.Application.Import.Jobs.ImportRuntime.CommandTimeoutSeconds ?? ProcessCommandTimeoutSeconds) is { } seconds)
        {
            var builder = new SqlConnectionStringBuilder(_resolvedConnStr) { CommandTimeout = seconds };
            return new SqlConnection(builder.ConnectionString);
        }

        return new SqlConnection(_resolvedConnStr);
    }

    /// <summary>
    /// See <see cref="ITenantConnectionFactory.CreateReadOnlyAsync"/>. Primary unless the replica is switched on for the tenant AND its
    /// heartbeat says it is close enough behind; any doubt (no lag monitor, unknown lag, an error) means primary.
    /// </summary>
    public async Task<SqlConnection> CreateReadOnlyAsync(ReadReplicaIntent intent = ReadReplicaIntent.Interactive, CancellationToken ct = default)
    {
        if (_lagMonitor is null || _configuration is null || _queryContext.TenantId == 0) return await CreateAsync(ct);

        var options = Compute.ComputeConfig.ReadReplica(_configuration);
        if (!options.Enabled) return await CreateAsync(ct);

        var readOnly = await _resolver.ResolveReadOnlyAsync(_queryContext.TenantId, ct);
        if (readOnly is null) return await CreateAsync(ct);

        var lag = await _lagMonitor.GetLagSecondsAsync(_queryContext.TenantId, readOnly, ct);
        return ReadReplicaRouting.IsTooFarBehind(lag, intent, options) ? await CreateAsync(ct) : new SqlConnection(readOnly);
    }
}
