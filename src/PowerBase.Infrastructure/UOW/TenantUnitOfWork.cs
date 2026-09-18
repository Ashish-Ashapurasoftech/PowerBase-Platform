using System.Data;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.UOW;

public class TenantUnitOfWork : ITenantUnitOfWork
{
    private readonly ITenantConnectionFactory _factory;
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;
    private bool _committed;

    public TenantUnitOfWork(ITenantConnectionFactory factory)
    {
        _factory = factory;
    }

    public IDbConnection Connection => _connection
        ?? throw new InvalidOperationException("BeginAsync must be called before accessing Connection.");
    public IDbTransaction? Transaction => _transaction;

    public async Task BeginAsync(CancellationToken ct = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A transaction is already active.");

        // A scoped tenant UoW is reused by long-running pipeline actions. Always release a
        // completed connection before acquiring the next one; otherwise every copied row keeps
        // one pooled SQL connection until the entire action scope is disposed.
        await ReleaseAsync();
        try
        {
            _connection = await _factory.CreateAsync(ct);
            await _connection.OpenAsync(ct);
            _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(ct);
            _committed = false;
        }
        catch
        {
            await ReleaseAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken ct = default) 
    {
        if (_transaction is null) throw new InvalidOperationException("No active transaction.");
        await _transaction.CommitAsync(ct);
        _committed = true;
        await ReleaseAsync();
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_transaction is null) return;
        try
        {
            if (!_committed) await _transaction.RollbackAsync(ct);
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    private async Task ReleaseAsync()
    {
        var transaction = _transaction;
        var connection = _connection;
        _transaction = null;
        _connection = null;
        if (transaction is not null) await transaction.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _connection?.Dispose();
    }
}
