using Microsoft.Data.SqlClient;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Pipelines;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>Runs only when POWERBASE_TEST_SQL names a SQL Server (a connection string with rights on tempdb); skipped otherwise.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string Variable = "POWERBASE_TEST_SQL";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to a SQL Server connection string to run this test.";
    }
}

/// <summary>
/// The write-time projector's reads must not wait on the lock of the transaction that has just written the record
/// (that wait was the 30–90 s per record). Proven against a real SQL Server with a global temp table in tempdb.
/// </summary>
public class ReadUncommittedConnectionTests
{
    private sealed class Factory(string connectionString) : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(connectionString));
    }

    private static string ConnectionString => Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)!;

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, SqlTransaction? tx = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        return await command.ExecuteScalarAsync();
    }

    [SqlServerFact]
    public async Task WriterHoldsALock_OrdinaryReadWaits_ButTheWriteTimeReadDoesNot_AndSeesTheUncommittedValue()
    {
        var table = $"##pb_locktest_{Guid.NewGuid():N}";
        await using var writer = new SqlConnection(ConnectionString);
        await writer.OpenAsync();
        await ScalarAsync(writer, $"CREATE TABLE {table}(Id INT PRIMARY KEY, V INT); INSERT {table} VALUES (1, 100);");
        await using var tx = (SqlTransaction)await writer.BeginTransactionAsync();
        await ScalarAsync(writer, $"UPDATE {table} SET V = 1000 WHERE Id = 1", tx);   // uncommitted, row locked

        try
        {
            // An ordinary connection waits on that lock (here: gives up after 1 s instead of SQL Server's 30 s).
            await using (var ordinary = new SqlConnection(ConnectionString))
            {
                await ordinary.OpenAsync();
                await ScalarAsync(ordinary, "SET LOCK_TIMEOUT 1000");
                var blocked = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(ordinary, $"SELECT V FROM {table} WHERE Id = 1"));
                Assert.Equal(1222, blocked.Number);
            }

            // The write-time connection reads straight through, and sees the step's own uncommitted write.
            await using var readUncommitted = await new ReadUncommittedTenantConnectionFactory(new Factory(ConnectionString)).CreateAsync();
            var started = DateTime.UtcNow;
            var value = await ScalarAsync(readUncommitted, $"SELECT V FROM {table} WHERE Id = 1");
            Assert.Equal(1000, Convert.ToInt32(value));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "the read must not wait on the writer's lock");
        }
        finally
        {
            await tx.RollbackAsync();
            await ScalarAsync(writer, $"DROP TABLE {table}");
        }
    }

    [SqlServerFact]
    public async Task WriteTimeConnections_AreOpenedWithTheirOwnPool_SoTheIsolationLevelNeverLeaksToOrdinaryOnes()
    {
        await using var readUncommitted = await new ReadUncommittedTenantConnectionFactory(new Factory(ConnectionString)).CreateAsync();
        Assert.EndsWith(".WriteTimeReads", new SqlConnectionStringBuilder(readUncommitted.ConnectionString).ApplicationName);
        // No command timeout: a read of millions of rows takes as long as it takes.
        await using (var probe = readUncommitted.CreateCommand()) Assert.Equal(0, probe.CommandTimeout);
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(readUncommitted,
            "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID")));   // 1 = READ UNCOMMITTED

        await using var ordinary = new SqlConnection(ConnectionString);
        await ordinary.OpenAsync();
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(ordinary,
            "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID")));   // 2 = READ COMMITTED
    }
}
