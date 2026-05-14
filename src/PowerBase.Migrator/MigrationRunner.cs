using Microsoft.Data.SqlClient;

namespace PowerBase.Migrator;

public class MigrationRunner
{
    private readonly string _connectionString;
    private readonly string _migrationsPath;

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

    public MigrationRunner(string connectionString, string migrationsPath)
    {
        _connectionString = connectionString;
        _migrationsPath = migrationsPath;
    }

    public async Task RunAsync()
    {
        var scripts = Directory
            .GetFiles(_migrationsPath, "*.sql")
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        if (scripts.Count == 0)
        {
            Console.WriteLine("No migration scripts found.");
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await EnsureMigrationsTableAsync(connection);

        var applied = 0;
        var skipped = 0;

        foreach (var scriptPath in scripts)
        {
            var scriptName = Path.GetFileName(scriptPath);

            if (await IsAppliedAsync(connection, scriptName))
            {
                Console.WriteLine($"  [skip]  {scriptName}");
                skipped++;
                continue;
            }

            Console.Write($"  [run]   {scriptName} ... ");
            await RunScriptAsync(connection, scriptPath, scriptName);
            Console.WriteLine("done");
            applied++;
        }

        Console.WriteLine();
        Console.WriteLine($"Applied: {applied}  Skipped: {skipped}  Total: {scripts.Count}");
    }

    private static async Task EnsureMigrationsTableAsync(SqlConnection connection)
    {
        await using var cmd = new SqlCommand(EnsureTableSql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsAppliedAsync(SqlConnection connection, string scriptName)
    {
        await using var cmd = new SqlCommand(IsAppliedSql, connection);
        cmd.Parameters.AddWithValue("@scriptName", scriptName);
        var count = (int)(await cmd.ExecuteScalarAsync())!;
        return count > 0;
    }

    private static async Task RunScriptAsync(SqlConnection connection, string scriptPath, string scriptName)
    {
        var sql = await File.ReadAllTextAsync(scriptPath);

        // Split on GO batch separators (SQL Server convention)
        var batches = sql
            .Split(["\nGO", "\r\nGO"], StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            foreach (var batch in batches)
            {
                await using var cmd = new SqlCommand(batch, connection, transaction);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync();
            }

            await using var record = new SqlCommand(RecordSql, connection, transaction);
            record.Parameters.AddWithValue("@scriptName", scriptName);
            await record.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
