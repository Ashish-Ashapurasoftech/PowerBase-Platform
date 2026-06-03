using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PowerBase.Infrastructure.Migrations;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var controlConnectionString = config.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

var dbRoot = FindDatabaseRoot();
var rootMigrationsPath    = Path.Combine(dbRoot, "migrations");
var controlMigrationsPath = Path.Combine(dbRoot, "migrations", "control");
var tenantMigrationsPath  = Path.Combine(dbRoot, "migrations", "tenant");

// Usage:
//   dotnet run                           → root migrations (backwards-compat)
//   dotnet run -- migrate control        → control-plane migrations
//   dotnet run -- migrate tenant --id N  → tenant baseline against Powerbase_N
//   dotnet run -- migrate tenants        → tenant baseline against ALL provisioned tenant DBs

Console.WriteLine("PowerBase Migrator");
Console.WriteLine($"Server   : {MaskConnectionString(controlConnectionString)}");
Console.WriteLine();

try
{
    var exitCode = args is ["migrate", "control"]
        ? await RunControlAsync()
        : args is ["migrate", "tenant", "--id", var idStr]
            ? await RunSingleTenantAsync(idStr)
            : args is ["migrate", "tenants"]
                ? await RunAllTenantsAsync()
                : await RunRootAsync();

    return exitCode;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Migration failed: {ex.Message}");
    Console.ResetColor();
    return 1;
}

// ── modes ────────────────────────────────────────────────────────────────────

async Task<int> RunRootAsync()
{
    Console.WriteLine($"Mode     : root migrations ({rootMigrationsPath})");
    Console.WriteLine();
    await MigrationRunner.RunAsync(controlConnectionString, rootMigrationsPath);
    return 0;
}

async Task<int> RunControlAsync()
{
    Console.WriteLine($"Mode     : control migrations ({controlMigrationsPath})");
    Console.WriteLine();
    await MigrationRunner.RunAsync(controlConnectionString, controlMigrationsPath, "Control");
    return 0;
}

async Task<int> RunSingleTenantAsync(string idStr)
{
    if (!long.TryParse(idStr, out var tenantId))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Invalid tenant id: {idStr}");
        Console.ResetColor();
        return 1;
    }

    var (databaseName, _) = await GetTenantRoutingAsync(tenantId);
    var tenantCs = BuildTenantConnectionString(controlConnectionString, databaseName);
    Console.WriteLine($"Mode     : tenant migrations → {databaseName}");
    Console.WriteLine();
    await MigrationRunner.RunAsync(tenantCs, tenantMigrationsPath, $"Tenant {tenantId}");
    return 0;
}

async Task<int> RunAllTenantsAsync()
{
    Console.WriteLine("Mode     : tenant migrations → all provisioned tenants");
    Console.WriteLine();

    var tenants = await GetAllProvisionedTenantsAsync();
    if (tenants.Count == 0)
    {
        Console.WriteLine("No provisioned tenants found.");
        return 0;
    }

    var errors = 0;
    foreach (var (id, dbName) in tenants)
    {
        Console.WriteLine($"── Tenant {id} ({dbName}) ──────────────────────────");
        try
        {
            var tenantCs = BuildTenantConnectionString(controlConnectionString, dbName);
            await MigrationRunner.RunAsync(tenantCs, tenantMigrationsPath, $"Tenant {id}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FAILED: {ex.Message}");
            Console.ResetColor();
            errors++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Tenants processed: {tenants.Count}  Errors: {errors}");
    return errors > 0 ? 1 : 0;
}

// ── helpers ──────────────────────────────────────────────────────────────────

async Task<(string DatabaseName, string? ServerRef)> GetTenantRoutingAsync(long tenantId)
{
    const string sql = """
        SELECT DatabaseName, ServerRef
        FROM meta.Tenant
        WHERE Id = @id AND ProvisioningState = 'Ready'
        """;

    await using var conn = new SqlConnection(controlConnectionString);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("@id", tenantId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        throw new InvalidOperationException($"Tenant {tenantId} not found or not in Ready state.");

    var dbName = reader.GetString(0);
    var serverRef = reader.IsDBNull(1) ? null : reader.GetString(1);
    return (dbName, serverRef);
}

async Task<List<(long Id, string DatabaseName)>> GetAllProvisionedTenantsAsync()
{
    const string sql = """
        SELECT Id, DatabaseName
        FROM meta.Tenant
        WHERE ProvisioningState = 'Ready' AND DatabaseName IS NOT NULL
        ORDER BY Id
        """;

    var result = new List<(long, string)>();
    await using var conn = new SqlConnection(controlConnectionString);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(sql, conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        result.Add((reader.GetInt64(0), reader.GetString(1)));
    return result;
}

static string BuildTenantConnectionString(string baseConnectionString, string databaseName)
{
    var builder = new SqlConnectionStringBuilder(baseConnectionString)
    {
        InitialCatalog = databaseName
    };
    return builder.ConnectionString;
}

static string FindDatabaseRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "database");
        if (Directory.Exists(candidate))
            return candidate;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException(
        "Could not locate 'database/' directory. Run from within the repository.");
}

static string MaskConnectionString(string cs)
{
    var builder = new SqlConnectionStringBuilder(cs);
    return $"Server={builder.DataSource};Database={builder.InitialCatalog}";
}
