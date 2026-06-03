using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.Persistence;

public class TenantConnectionFactory : ITenantConnectionFactory
{
    private readonly ITenantConnectionResolver _resolver;
    private readonly IQueryContext _queryContext;
    private string? _resolvedConnStr;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TenantConnectionFactory(ITenantConnectionResolver resolver, IQueryContext queryContext)
    {
        _resolver = resolver;
        _queryContext = queryContext;
    }

    public async Task<SqlConnection> CreateAsync(CancellationToken ct = default)
    {
        if (_queryContext.TenantId == 0)
            throw new InvalidOperationException("No tenant in scope. TenantId is 0.");

        if (_resolvedConnStr is null)
        {
            await _lock.WaitAsync(ct);
            try
            {
                _resolvedConnStr ??= await _resolver.ResolveAsync(_queryContext.TenantId, ct);
            }
            finally
            {
                _lock.Release();
            }
        }

        return new SqlConnection(_resolvedConnStr);
    }
}
