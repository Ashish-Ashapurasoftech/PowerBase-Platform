using Microsoft.Extensions.Configuration;
using PowerBase.QbMigration.Worker;

// QuickBase -> PowerBase migration worker. Usage (all commands take --tenant <id>):
//   init                                         create the qbm schema in that tenant only
//   map      --qb-app <dbid> --pb-app <id> --run-as <userId>
//   plan     --run <id> [--extend]              --extend plans only records created in QuickBase since the plan
//   run      --run <id> [--queue local|servicebus] [--reload]   --reload loads done chunks again (after users/fixes)
//   users    --run <id> --app-role <id> --tenant-role <id> [--apply]
//   finalize --run <id>
//   validate --run <id> [--sample 1.0]           gates 3, 4, 7 (sample 1 = compare every record)
//   reindex  --run <id> --api <PowerBase API url>  needs PowerBase:ApiToken (super admin user token)
//   report   --run <id> [--out <folder or .xlsx>]   Excel report of every run of that app
//
// Operator commands (docs/qb-migration/operator-guide.md):
//   migrate  --qb-app <dbid> --pb-app <id> --run-as <userId> [--new]   start (or continue the latest run of that app)
//   migrate  --run <id>                          continue a run: plan -> load -> finalize -> validate -> report
//            [--queue local|servicebus] [--sample 1.0] [--out <folder or .xlsx>]
//   pause | resume | stop  --run <id>            the running worker finishes the chunks in progress first
//   status   --run <id>                          progress, speed, dead letters, chunks to fix first
//   retry-dlq --run <id> [--list] [--chunk <id,id>] [--no-continue]   retry chunks the queue gave up on, then continue
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("commands: migrate | pause | resume | stop | status | retry-dlq | report");
    Console.WriteLine("          init | map | plan | run | users | finalize | validate | reindex   (see Program.cs for options)");
    return 0;
}

var options = ParseOptions(args.Skip(1).ToArray());
var builder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddUserSecrets<Commands>(optional: true)
    .AddEnvironmentVariables("QBM_");
IConfiguration config = builder.Build();
// On the Azure host the secrets live in the migration's Key Vault; environment variables still win, for one-off overrides.
if (config["Migration:KeyVaultUri"] is { Length: > 0 } vault)
    config = builder.AddInMemoryCollection(await KeyVaultSettings.LoadAsync(new Uri(vault))).AddEnvironmentVariables("QBM_").Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// A service manager stopping the worker (SIGTERM) cancels like Ctrl+C: uncommitted chunks roll back and stay not done.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,
    signal => { signal.Cancel = true; cts.Cancel(); });

using var composition = new Composition(config, Required("tenant"));
var commands = new Commands(composition);
// Service Bus when the host has it (the Azure worker), the in-process queue otherwise.
var queue = options.GetValueOrDefault("queue") ?? (string.IsNullOrWhiteSpace(config["ServiceBus:ConnectionString"]) ? "local" : "servicebus");
var sample = double.Parse(options.GetValueOrDefault("sample") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
try
{
    switch (args[0])
    {
        case "init": await commands.InitAsync(cts.Token); break;
        case "map": await commands.MapAsync(Text("qb-app"), Required("pb-app"), Required("run-as"), cts.Token); break;
        case "plan": await commands.PlanAsync(Required("run"), options.ContainsKey("extend"), cts.Token); break;
        case "run": return await commands.RunAsync(Required("run"), options.GetValueOrDefault("queue") ?? "local", options.ContainsKey("reload"), cts.Token) ? 0 : 2;
        case "users": await commands.UsersAsync(Required("run"), Required("app-role"), Required("tenant-role"), options.ContainsKey("apply"), cts.Token); break;
        case "finalize": return await commands.FinalizeAsync(Required("run"), cts.Token) ? 0 : 2;
        case "validate": return await commands.ValidateAsync(Required("run"), double.Parse(options.GetValueOrDefault("sample") ?? "1", System.Globalization.CultureInfo.InvariantCulture), cts.Token) ? 0 : 2;
        case "reindex": await commands.ReindexAsync(Required("run"), Text("api"), cts.Token); break;
        case "report": await commands.ReportAsync(Required("run"), options.GetValueOrDefault("out"), cts.Token); break;
        case "migrate":
            return await commands.MigrateAsync(Optional("run"), options.GetValueOrDefault("qb-app"), Optional("pb-app"), Optional("run-as"),
                options.ContainsKey("new"), queue, sample, options.GetValueOrDefault("out"), cts.Token);
        case "pause" or "resume" or "stop": await commands.SetControlAsync(Required("run"), args[0], cts.Token); break;
        case "status": await commands.StatusAsync(Required("run"), cts.Token); break;
        case "retry-dlq":
            return await commands.RetryDeadLettersAsync(Required("run"), options.ContainsKey("list"),
                options.GetValueOrDefault("chunk") is { Length: > 0 } ids ? ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList() : null,
                !options.ContainsKey("no-continue"), queue, sample, options.GetValueOrDefault("out"), cts.Token);
        default: Console.Error.WriteLine($"Unknown command '{args[0]}'."); return 1;
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. Chunks that weren't committed stay not done; run again to continue.");
    return 3;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

long? Optional(string name) =>
    options.TryGetValue(name, out var v) && v is not null ? (long.TryParse(v, out var n) ? n : throw new ArgumentException($"--{name} must be a number.")) : null;

long Required(string name) =>
    options.TryGetValue(name, out var v) && long.TryParse(v, out var n) ? n : throw new ArgumentException($"--{name} <number> is required.");

string Text(string name) =>
    options.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"--{name} <value> is required.");

static Dictionary<string, string?> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var name = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
        result[name] = hasValue ? args[++i] : null;
    }
    return result;
}
