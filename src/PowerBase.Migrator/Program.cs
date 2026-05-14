using Microsoft.Extensions.Configuration;
using PowerBase.Migrator;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var connectionString = config.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

var migrationsPath = FindMigrationsPath();

Console.WriteLine("PowerBase Migrator");
Console.WriteLine($"Database : {MaskConnectionString(connectionString)}");
Console.WriteLine($"Scripts  : {migrationsPath}");
Console.WriteLine();

try
{
    var runner = new MigrationRunner(connectionString, migrationsPath);
    await runner.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Migration failed: {ex.Message}");
    Console.ResetColor();
    return 1;
}

static string FindMigrationsPath()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "database", "migrations");
        if (Directory.Exists(candidate))
            return candidate;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException(
        "Could not locate 'database/migrations'. Run from within the repository.");
}

static string MaskConnectionString(string cs)
{
    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
    return $"Server={builder.DataSource};Database={builder.InitialCatalog}";
}
