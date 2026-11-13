using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

public sealed class ConnectedPendingImportRepository(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IConnectedPendingImportRepository
{
    private const string Columns = """
        Id, AppId, AppTableId, SourceAppName, SourceTableName, PayloadJson, Status, Reason, AttemptCount,
        CreatedOn, LastAttemptOn, ResolvedOn, ConnectedTablePublicId
        """;

    private const string FindOpenByTableSql = """
        SELECT Id FROM connected.PendingImportConnection WHERE AppTableId = @appTableId AND Status = 'pending'
        """;

    private const string InsertSql = """
        INSERT INTO connected.PendingImportConnection (AppId, AppTableId, SourceAppName, SourceTableName, PayloadJson, Reason, LastAttemptOn)
        VALUES (@AppId, @AppTableId, @SourceAppName, @SourceTableName, @PayloadJson, @Reason, SYSUTCDATETIME())
        """;

    private const string UpdateOpenSql = """
        UPDATE connected.PendingImportConnection
        SET SourceAppName = @SourceAppName, SourceTableName = @SourceTableName, PayloadJson = @PayloadJson,
            Reason = @Reason, AttemptCount = 0, LastAttemptOn = SYSUTCDATETIME()
        WHERE Id = @Id
        """;

    private const string ListPendingSql = $"SELECT {Columns} FROM connected.PendingImportConnection WHERE Status = 'pending' ORDER BY CreatedOn";

    private const string MarkResolvedSql = """
        UPDATE connected.PendingImportConnection
        SET Status = 'resolved', ResolvedOn = SYSUTCDATETIME(), ConnectedTablePublicId = @connectedTablePublicId, Reason = NULL
        WHERE Id = @id
        """;

    private const string RecordAttemptSql = """
        UPDATE connected.PendingImportConnection
        SET AttemptCount = AttemptCount + 1, LastAttemptOn = SYSUTCDATETIME(), Reason = @reason
        WHERE Id = @id AND Status = 'pending'
        """;

    public async Task UpsertAsync(ConnectedPendingImportConnection pending, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var existingId = await conn.ExecuteScalarAsync<long?>(
            new CommandDefinition(FindOpenByTableSql, new { appTableId = pending.AppTableId }, cancellationToken: ct));
        if (existingId is { } id)
            await conn.ExecuteAsync(new CommandDefinition(UpdateOpenSql, new
            {
                Id = id, pending.SourceAppName, pending.SourceTableName, pending.PayloadJson, pending.Reason,
            }, cancellationToken: ct));
        else
            await conn.ExecuteAsync(new CommandDefinition(InsertSql, pending, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectedPendingImportConnection>> ListPendingAsync(CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ConnectedPendingImportConnection>(new CommandDefinition(ListPendingSql, cancellationToken: ct))).AsList();
    }

    public async Task MarkResolvedAsync(long id, Guid connectedTablePublicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(MarkResolvedSql, new { id, connectedTablePublicId }, cancellationToken: ct));
    }

    public async Task RecordAttemptAsync(long id, string reason, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(RecordAttemptSql, new { id, reason = reason.Length <= 500 ? reason : reason[..500] }, cancellationToken: ct));
    }
}
