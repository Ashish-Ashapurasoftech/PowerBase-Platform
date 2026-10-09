using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Pipelines;

/// <summary>Opens each connection with READ UNCOMMITTED, so a read never waits on a lock held by an open
/// transaction (see <see cref="IPipelineWriteTimeRelationalProjector"/>). The isolation level is per physical
/// connection; SqlClient resets it when the connection returns to the pool.</summary>
public sealed class ReadUncommittedTenantConnectionFactory(ITenantConnectionFactory inner) : ITenantConnectionFactory
{
    public async Task<SqlConnection> CreateAsync(CancellationToken ct = default)
    {
        // A pool of its own (the pool is keyed by connection string): READ UNCOMMITTED must never be inherited by an
        // ordinary request that happens to reuse the physical connection.
        var template = await inner.CreateAsync(ct);
        var builder = new SqlConnectionStringBuilder(template.ConnectionString)
        {
            // Bounded: this runs inside the step's open write transaction, so an unindexed scan over millions of
            // child rows must fail (the projector then leaves those values out) instead of holding the transaction open forever.
            CommandTimeout = 300,
            ApplicationName = $"{(string.IsNullOrWhiteSpace(new SqlConnectionStringBuilder(template.ConnectionString).ApplicationName) ? "PowerBase" : new SqlConnectionStringBuilder(template.ConnectionString).ApplicationName)}.WriteTimeReads"
        };
        await template.DisposeAsync();
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>The relationship projector for reads made while the pipeline's own write transaction is open: the
/// same <see cref="RelationalProjector"/>, over a record repository whose connections do not wait on locks.</summary>
public sealed class PipelineWriteTimeRelationalProjector : IPipelineWriteTimeRelationalProjector
{
    private readonly Lazy<RelationalProjector> _inner;

    public PipelineWriteTimeRelationalProjector(
        ITenantConnectionFactory connectionFactory,
        IQueryContext queryContext,
        IMessagePublisher messagePublisher,
        IEncryptionService encryptionService,
        IControlConnectionFactory controlConnectionFactory,
        IServiceProvider services,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRelationshipRepository relationshipRepo,
        IAppRepository appRepo,
        IUserRepository userRepo,
        IFormulaProjector formulaProjector)
    {
        _inner = new Lazy<RelationalProjector>(() =>
        {
            var records = new RecordRepository(new ReadUncommittedTenantConnectionFactory(connectionFactory), queryContext,
                messagePublisher, encryptionService, controlConnectionFactory, services);
            return new RelationalProjector(tableRepo, fieldRepo, records, relationshipRepo, appRepo, userRepo, formulaProjector, queryContext);
        });
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<long, object?>>> ProjectAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default) =>
        _inner.Value.ProjectAsync(table, fields, rows, ct);
}
