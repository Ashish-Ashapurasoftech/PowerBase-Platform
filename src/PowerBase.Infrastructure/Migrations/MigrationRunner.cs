using Microsoft.Data.SqlClient;

namespace PowerBase.Infrastructure.Migrations;

public static class MigrationRunner
{
    private const string EnsureTableSql = """
        IF NOT EXISTS (
            SELECT 1 FROM sys.tables
            WHERE name = '_migrations' AND schema_id = SCHEMA_ID('dbo'))
        CREATE TABLE dbo._migrations (
            ScriptName VARCHAR(200)  NOT NULL CONSTRAINT PK__migrations PRIMARY KEY,
            AppliedOn  DATETIME2(3) NOT NULL CONSTRAINT DF__migrations_AppliedOn DEFAULT SYSUTCDATETIME()
        )
        """;

    private const string IsAppliedSql =
        "SELECT COUNT(1) FROM dbo._migrations WHERE ScriptName = @scriptName";

    private const string RecordSql =
        "INSERT INTO dbo._migrations (ScriptName) VALUES (@scriptName)";

    /// <summary>
    /// Runs all *.sql scripts in <paramref name="migrationsFolder"/> against
    /// <paramref name="connectionString"/> in filename order, skipping already-applied ones.
    /// Returns the number of scripts newly applied.
    /// </summary>
    public static async Task<int> RunAsync(
        string connectionString,
        string migrationsFolder,
        string label = "",
        CancellationToken ct = default,
        int commandTimeoutSeconds = 120)
    {
        var scripts = Directory
            .GetFiles(migrationsFolder, "*.sql")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        if (scripts.Count == 0)
        {
            Console.WriteLine($"  No migration scripts found in {migrationsFolder}");
            return 0;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await EnsureMigrationsTableAsync(connection, commandTimeoutSeconds, ct);

        var applied = 0;
        var skipped = 0;

        foreach (var scriptPath in scripts)
        {
            var scriptName = Path.GetFileName(scriptPath);

            if (await IsAppliedAsync(connection, scriptName, commandTimeoutSeconds, ct))
            {
                Console.WriteLine($"  [skip]  {scriptName}");
                skipped++;
                continue;
            }

            Console.Write($"  [run]   {scriptName} ... ");
            await RunScriptAsync(connection, scriptPath, scriptName, commandTimeoutSeconds, ct);
            Console.WriteLine("done");
            applied++;
        }

        Console.WriteLine();
        if (!string.IsNullOrEmpty(label))
            Console.Write($"{label}: ");
        Console.WriteLine($"Applied: {applied}  Skipped: {skipped}  Total: {scripts.Count}");
        return applied;
    }

    private static async Task EnsureMigrationsTableAsync(SqlConnection connection, int commandTimeout, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(EnsureTableSql, connection) { CommandTimeout = commandTimeout };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsAppliedAsync(SqlConnection connection, string scriptName, int commandTimeout, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(IsAppliedSql, connection) { CommandTimeout = commandTimeout };
        cmd.Parameters.AddWithValue("@scriptName", scriptName);
        var count = (int)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }

    private static async Task RunScriptAsync(SqlConnection connection, string scriptPath, string scriptName, int commandTimeout, CancellationToken ct)
    {
        var sql = await File.ReadAllTextAsync(scriptPath, ct);

        var batches = sql
            .Split(["\nGO", "\r\nGO"], StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var batch in batches)
            {
                await using var cmd = new SqlCommand(batch, connection, transaction);
                cmd.CommandTimeout = commandTimeout;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using var record = new SqlCommand(RecordSql, connection, transaction);
            record.Parameters.AddWithValue("@scriptName", scriptName);
            await record.ExecuteNonQueryAsync(ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
