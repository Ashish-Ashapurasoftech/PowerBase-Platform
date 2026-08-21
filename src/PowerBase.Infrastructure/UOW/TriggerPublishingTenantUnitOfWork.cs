using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Infrastructure.UOW;

public class TriggerPublishingTenantUnitOfWork : ITenantUnitOfWork
{
    private readonly ITenantUnitOfWork _inner;
    private readonly List<Func<Task>> _postCommitActions = new();

    public TriggerPublishingTenantUnitOfWork(ITenantUnitOfWork inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public IDbConnection Connection => _inner.Connection;
    public IDbTransaction? Transaction => _inner.Transaction;

    public void RegisterPostCommitAction(Func<Task> action)
    {
        if (action != null)
        {
            _postCommitActions.Add(action);
        }
    }

    public async Task BeginAsync(CancellationToken ct = default)
    {
        await _inner.BeginAsync(ct);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await _inner.CommitAsync(ct);

        // Execute post-commit actions.
        foreach (var action in _postCommitActions)
        {
            try
            {
                await action();
            }
            catch
            {
                // Do not throw/rollback since DB commit succeeded.
            }
        }
        _postCommitActions.Clear();
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        await _inner.RollbackAsync(ct);
        _postCommitActions.Clear();
    }

    public void Dispose()
    {
        _inner.Dispose();
        _postCommitActions.Clear();
    }
}
