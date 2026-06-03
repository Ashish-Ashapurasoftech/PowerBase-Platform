using System.Data;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.UOW;

public class ControlUnitOfWork : IControlUnitOfWork
{
    private readonly SqlConnection _connection;
    private SqlTransaction? _transaction;
    private bool _committed;

    public ControlUnitOfWork(IControlConnectionFactory factory)
    {
        _connection = factory.Create();
    }

    public IDbConnection Connection => _connection;
    public IDbTransaction? Transaction => _transaction;

    public async Task BeginAsync(CancellationToken ct = default)
    {
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(ct);
        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(ct);
        _committed = false;
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_transaction is null) throw new InvalidOperationException("No active transaction.");
        await _transaction.CommitAsync(ct);
        _committed = true;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_transaction is null || _committed) return;
        await _transaction.RollbackAsync(ct);
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _connection.Dispose();
    }
}
