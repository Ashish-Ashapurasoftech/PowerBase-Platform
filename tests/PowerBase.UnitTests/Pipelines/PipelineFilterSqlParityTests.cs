using System.Globalization;
using System.Reflection;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Repositories;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// One filter rule, three places that read it: a trigger's filter (in memory, on the values the event carries), Search Records
/// (the SQL the rule maps to) and Search Records' in-memory half (an encrypted app, a computed field). For every stored field type,
/// every operator the filter editor offers for it and a spread of stored values, all three must pick the same records.
/// The SQL half runs on a real SQL Server (a #temp table in tempdb): set PB_PARITY_SQL to a connection string to run it, e.g.
/// "Server=.\SQLEXPRESS;Database=tempdb;Trusted_Connection=true;Encrypt=false". Without it the test does nothing.
/// </summary>
public class PipelineFilterSqlParityTests
{
    private readonly ITestOutputHelper _output;
    public PipelineFilterSqlParityTests(ITestOutputHelper output) => _output = output;

    internal sealed record TypeCase(string TypeCode, string SqlType, string Group, object?[] Stored, string[] Operands, string? Settings = null);

    /// <summary>A Date / Numeric Range value: its start column (f_N) and end column (f_N_e).</summary>
    internal sealed record RangeValue(object? Start, object? End)
    {
        /// <summary>As a trigger carries it: the range's JSON, or null when both ends are blank.</summary>
        public string? AsJson() => Start is null && End is null ? null
            : System.Text.Json.JsonSerializer.Serialize(new { start = PipelineFilterSqlParityTests.AsSubmitted(Start), end = PipelineFilterSqlParityTests.AsSubmitted(End) });
    }

    private static readonly string[] TextOperands = ["apple", "Apple pie", "pie", "a_b", "A*", "?pple*"];
    private static readonly object?[] TextValues = [null, "", "Apple", "apple pie", "Banana", "a_b", "axb"];
    private static readonly string[] NumberOperands = ["5", "5.5", "12", "0", "-3"];
    private static readonly string[] DateOperands = ["2026-10-15", "2026-10-14T18:30:00.000Z", "2026-10-15T00:00:00.000Z"];

    internal static readonly TypeCase[] Cases =
    [
        new("Text", "NVARCHAR(MAX)", "text", TextValues, TextOperands),
        new("TextMultiLine", "NVARCHAR(MAX)", "text", TextValues, TextOperands),
        // The record form's editor saves HTML: "<p>Apple</p>"; an emptied one "<p><br></p>".
        new("RichText", "NVARCHAR(MAX)", "text", [null, "", "<p>Apple</p>", "<p>apple <b>pie</b></p>", "<p><br></p>", "<p>Fish &amp; Chips</p>", "Banana"],
            ["apple", "Apple pie", "pie", "Fish & Chips", "A*"]),
        new("Email", "NVARCHAR(MAX)", "text", [null, "", "a@x.com", "B@Y.com"], ["a@x.com", "x.com", "*@x.com"]),
        new("Phone", "NVARCHAR(MAX)", "text", [null, "", "9876543210", "+1 555"], ["98765", "9876543210", "+1*"]),
        new("Url", "NVARCHAR(MAX)", "text", [null, "", "https://a.com/x", "http://b.org"], ["https", "https://a.com/x", "*.org"]),
        new("SingleSelect", "NVARCHAR(MAX)", "text", [null, "", "Open", "Closed"], ["Open", "open", "Clo", "O*"], "{\"options\":[\"Open\",\"Closed\"]}"),
        new("MultiSelect", "NVARCHAR(MAX)", "multiSelect", [null, "", "[\"Red\"]", "[\"Red\",\"Blue\"]", "[\"Bred\"]", "Red,Blue"], ["Red", "Red,Blue", "Blue,Red", "Bred", "re"], "{\"options\":[\"Red\",\"Blue\",\"Bred\"]}"),
        new("Number", "DECIMAL(18,4)", "none", [null, 0m, 5m, 5.5m, 12m, -3m], NumberOperands),
        new("Currency", "DECIMAL(18,4)", "none", [null, 0m, 5m, 5.5m, 12m], NumberOperands),
        new("Percent", "DECIMAL(18,4)", "none", [null, 0m, 5m, 5.5m], NumberOperands),
        new("Rating", "DECIMAL(18,4)", "none", [null, 0m, 3m, 5m], ["3", "5", "0"]),
        new("Duration", "INT", "none", [null, 0, 5, 3600], ["5", "3600", "0"]),
        new("Date", "DATE", "date", [null, new DateTime(2026, 10, 14), new DateTime(2026, 10, 15), new DateTime(2026, 10, 16)], DateOperands),
        new("DateTime", "DATETIME2(3)", "date", [null, new DateTime(2026, 10, 14, 20, 0, 0), new DateTime(2026, 10, 15, 0, 0, 0), new DateTime(2026, 10, 15, 10, 30, 0), new DateTime(2026, 10, 15, 23, 59, 0), new DateTime(2026, 10, 16, 5, 0, 0)], DateOperands),
        new("Time", "TIME", "none", [null, new TimeSpan(8, 0, 0), new TimeSpan(10, 30, 0), new TimeSpan(18, 45, 0)], ["10:30:00", "08:00:00", "10:30"]),
        new("Boolean", "BIT", "none", [null, false, true], ["true", "false"]),
        new("File", "NVARCHAR(MAX)", "file", [null, "", "[{\"name\":\"Report.pdf\",\"path\":\"x/y.pdf\"}]", "[{\"name\":\"a.txt\"},{\"name\":\"b.pdf\"}]", "http://x/legacy.pdf"], ["report.pdf", "pdf", "rep", "*.pdf", "b.pdf"]),
        new("Address", "NVARCHAR(MAX)", "address", [null, "", "{\"street1\":\"1 Main St\",\"city\":\"Surat\",\"state\":\"GJ\",\"postalCode\":\"395001\",\"country\":\"India\"}"], ["Surat", "Main", "*Surat*"]),
        // A blank User / User List is written as NULL (RecordRepository: a blank value of a type that stores no text), never "".
        new("User", "NVARCHAR(MAX)", "user", [null, "40021", "40022"], ["40021", "4002*"]),
        new("MultiUser", "NVARCHAR(MAX)", "userList", [null, "[\"40021\"]", "[\"40021\",\"40022\"]", "40022,40023"], ["40021", "40021,40022", "40022,40021", "4002*"]),
        new("Reference", "BIGINT", "none", [null, 7L, 8L], ["7", "8"], "{\"parentTableId\":9}"),
        new("DateRange", "DATE", "none",
            [new RangeValue(null, null), new RangeValue(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)), new RangeValue(new DateTime(2026, 10, 15), new DateTime(2026, 10, 15)),
             new RangeValue(new DateTime(2026, 10, 16), new DateTime(2026, 10, 20)), new RangeValue(new DateTime(2026, 10, 10), null)],
            ["2026-10-15", "2026-10-16"]),
        new("NumericRange", "DECIMAL(18,4)", "none",
            [new RangeValue(null, null), new RangeValue(1m, 10m), new RangeValue(5m, 5m), new RangeValue(11m, 20m), new RangeValue(null, 7m)],
            ["5", "10", "11"]),
    ];

    /// <summary>What the trigger / Search Records filter editor saves (pipeline-filter-editor.component.ts, baseOperatorOptionsForField).</summary>
    internal static readonly Dictionary<string, string[]> BaseOperators = new()
    {
        ["TEXT"] = ["is", "is-not", "contains", "not-contains", "starts-with", "ends-with", "is-empty", "is-not-empty"],
        ["NUMBER"] = ["is", "is-not", "greater_than", "greater_than_or_equals", "less_than", "less_than_or_equals", "is-empty", "is-not-empty"],
        ["DATE"] = ["is", "is-not", "is-after", "is-on-or-after", "is-before", "is-on-or-before", "is-empty", "is-not-empty"],
        ["TIME"] = ["is", "is-not", "is-after", "is-on-or-after", "is-before", "is-on-or-before", "is-empty", "is-not-empty"],
        ["BOOLEAN"] = ["is_true", "is_false", "is", "is-not"],
    };

    internal static readonly Dictionary<string, string[]> ExtraOperators = new()
    {
        ["text"] = ["not-starts-with", "not-ends-with", "wildcard", "not-wildcard"],
        ["multiSelect"] = ["includes", "not-includes"],
        // "wildcard match" on a user field matches its person's name or email: Search Records works it out over the people first
        // (ResolveUserDetailFilterRulesAsync), outside this harness — PipelineUserFieldFilterTests covers it.
        ["user"] = [],
        ["userList"] = ["includes", "not-includes"],
        ["file"] = ["not-starts-with", "not-ends-with"],
        ["address"] = ["wildcard", "not-wildcard"],
        ["date"] = [],
        ["none"] = [],
    };

    internal static readonly HashSet<string> Unary = ["is-empty", "is-not-empty", "is_true", "is_false"];

    internal static List<AppField> BuildFields() => Cases.Select((c, i) => new AppField
    {
        Id = 5000 + i, Fid = 10 + i, Name = $"f{10 + i}", Label = c.TypeCode, TypeCode = c.TypeCode, Settings = c.Settings,
    }).ToList();

    internal static PipelineEngine Engine()
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        return new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<IServiceScopeFactory>(), serviceProvider, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    internal static readonly MethodInfo Map = typeof(PipelineEngine).GetMethod("MapTriggerFilterGroupToDbFilterGroup", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo BuildWhere = typeof(RecordRepository).GetMethod("BuildFilterTreeWhere", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The value as a record form / the records API submits it (what a record-added trigger carries).</summary>
    internal static object? AsSubmitted(object? v) => v switch
    {
        null => null,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        int n => n.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTime dt when dt.TimeOfDay == TimeSpan.Zero => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        _ => v,
    };

    [Fact]
    public async Task EveryTypeOperatorAndValue_TriggerSqlAndInMemory_PickTheSameRecords()
    {
        var conn = Environment.GetEnvironmentVariable("PB_PARITY_SQL");
        if (string.IsNullOrWhiteSpace(conn)) return;

        var fields = BuildFields();
        var lookup = fields.ToDictionary(f => (long)f.Fid!.Value);
        await using var db = new SqlConnection(conn);
        await db.OpenAsync();
        var rows = await CreateTableAsync(db, fields);

        var engine = Engine();
        var report = new StringBuilder();
        var mismatches = 0;
        var checkedCount = 0;

        for (var i = 0; i < Cases.Length; i++)
        {
            var c = Cases[i];
            var field = fields[i];
            var fid = field.Fid!.Value;
            var category = PipelineFilterEvaluator.GetTypeCategory(field);
            var ops = BaseOperators[category].Concat(ExtraOperators[c.Group]).Distinct();
            var caseRows = rows.Where(r => r.CaseIndex == i).ToList();

            foreach (var op in ops)
                // A blank value too: a token that resolved to nothing, or a rule saved without one.
                foreach (var operand in Unary.Contains(op) ? [""] : c.Operands.Append(""))
                {
                    var group = new TriggerFilterGroup
                    {
                        LogicalOp = "AND",
                        Rules = [new TriggerFilterRule { Field = $"fid_{fid}", Operator = op, Value = operand }]
                    };

                    // Trigger: the values a deleted/bulk record carries (as read) and a submitted one (as typed).
                    var trigTyped = caseRows.Where(r => PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [fid] = r.Value is RangeValue rv ? rv.AsJson() : r.Value }, fields)).Select(r => r.Id).ToHashSet();
                    var trigText = caseRows.Where(r => PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [fid] = r.Value is RangeValue rs ? rs.AsJson() : AsSubmitted(r.Value) }, fields)).Select(r => r.Id).ToHashSet();

                    FilterGroup? tree;
                    try { tree = (FilterGroup?)Map.Invoke(engine, [group, fields, "{}", "path", new List<PipelineStep>(), null]); }
                    catch (TargetInvocationException ex) { report.AppendLine($"MAP-THROWS {c.TypeCode} {op} '{operand}': {ex.InnerException?.Message}"); mismatches++; continue; }

                    HashSet<long> sql;
                    if (tree is null) sql = caseRows.Select(r => r.Id).ToHashSet();
                    else
                    {
                        var parameters = new DynamicParameters();
                        var where = (string)BuildWhere.Invoke(null, [tree, parameters, lookup, null])!;
                        var ids = string.Join(",", caseRows.Select(r => r.Id));
                        try { sql = (await db.QueryAsync<long>($"SELECT Id FROM #pb WHERE IsDeleted = 0 AND Id IN ({ids}){where}", parameters)).ToHashSet(); }
                        catch (SqlException ex) { report.AppendLine($"SQL-ERROR {c.TypeCode} {op} '{operand}': {ex.Message} | {where}"); mismatches++; continue; }
                    }

                    var memory = tree is null
                        ? caseRows.Select(r => r.Id).ToHashSet()
                        : PipelineComputedFilter.Apply(
                                caseRows.Select(r => ((IReadOnlyDictionary<string, object?>)(r.Value is RangeValue mr
                                    ? new Dictionary<string, object?> { ["Id"] = r.Id, [$"f_{fid}"] = mr.Start, [$"f_{fid}_e"] = mr.End }
                                    : new Dictionary<string, object?> { ["Id"] = r.Id, [$"f_{fid}"] = r.Value }),
                                    (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>())), tree, fields)
                            .Select(p => (long)p.Row["Id"]!).ToHashSet();

                    // Search Records matches a Rich Text in memory (its HTML is not what SQL could read): that half is what it answers.
                    if (PipelineFilterEvaluator.IsRichTextField(field)) sql = memory;
                    checkedCount++;
                    if (trigTyped.SetEquals(sql) && trigText.SetEquals(sql) && memory.SetEquals(sql)) continue;
                    mismatches++;
                    string Show(HashSet<long> set) => "[" + string.Join(", ", caseRows.Where(r => set.Contains(r.Id)).Select(r => Fmt(r.Value))) + "]";
                    report.AppendLine($"{c.TypeCode,-14} {op,-22} '{operand}': sql={Show(sql)} memory={Show(memory)} trigger={Show(trigTyped)} triggerSubmitted={Show(trigText)}");
                }
        }

        _output.WriteLine($"checked {checkedCount}, mismatches {mismatches}");
        _output.WriteLine(report.ToString());
        var outFile = Environment.GetEnvironmentVariable("PB_PARITY_OUT");
        if (!string.IsNullOrWhiteSpace(outFile)) await File.WriteAllTextAsync(outFile, $"checked {checkedCount}, mismatches {mismatches}\n{report}");
        Assert.True(mismatches == 0, report.ToString());
    }

    /// <summary>A #temp table with a column per field (two for a range) and one row per stored value of each type, the other columns blank.</summary>
    private static async Task<List<(long Id, int CaseIndex, object? Value)>> CreateTableAsync(SqlConnection db, List<AppField> fields)
    {
        var cols = string.Join(", ", fields.Select((f, i) => $"f_{f.Fid} {Cases[i].SqlType} NULL"
            + (PowerBase.Domain.Constants.PhysicalNaming.IsRangeTypeCode(Cases[i].TypeCode) ? $", f_{f.Fid}_e {Cases[i].SqlType} NULL" : "")));
        await db.ExecuteAsync($"CREATE TABLE #pb (Id BIGINT NOT NULL PRIMARY KEY, IsDeleted BIT NOT NULL DEFAULT 0, {cols})");
        var rows = new List<(long Id, int CaseIndex, object? Value)>();
        long nextId = 1;
        for (var i = 0; i < Cases.Length; i++)
            foreach (var v in Cases[i].Stored)
            {
                var id = nextId++;
                rows.Add((id, i, v));
                var p = new DynamicParameters();
                p.Add("id", id);
                if (v is RangeValue range)
                {
                    p.Add("v", range.Start);
                    p.Add("e", range.End);
                    await db.ExecuteAsync($"INSERT #pb (Id, f_{fields[i].Fid}, f_{fields[i].Fid}_e) VALUES (@id, @v, @e)", p);
                    continue;
                }
                p.Add("v", v);
                await db.ExecuteAsync($"INSERT #pb (Id, f_{fields[i].Fid}) VALUES (@id, @v)", p);
            }
        return rows;
    }

    private static readonly string[] AdvancedOperators = ["EX", "XEX", "CT", "XCT", "SW", "XSW", "GT", "GTE", "LT", "LTE", "WC", "XWC", "HAS", "XHAS"];

    /// <summary>The Advanced Query operators that mean something on a type. Matching a day, a checkbox, a time or a range as text, a list
    /// operator on a single value, or a file / address / choice list in order has no answer to agree on, so those are left out.</summary>
    private static IEnumerable<string> AdvancedOperatorsFor(TypeCase c)
    {
        var equality = new[] { "EX", "XEX" };
        var order = new[] { "GT", "GTE", "LT", "LTE" };
        var text = new[] { "CT", "XCT", "SW", "XSW", "WC", "XWC" };
        if (PowerBase.Domain.Constants.PhysicalNaming.IsRangeTypeCode(c.TypeCode)) return equality.Concat(order);
        return c.TypeCode switch
        {
            "Date" or "DateTime" or "Time" => equality.Concat(order),
            "Boolean" => equality,
            "MultiSelect" or "MultiUser" => equality.Concat(text).Concat(["HAS", "XHAS"]),
            "File" or "Address" or "User" => equality.Concat(text),
            "Number" or "Currency" or "Percent" or "Rating" or "Duration" or "Reference" => equality.Concat(order).Concat(text),
            _ => equality.Concat(order).Concat(text),
        };
    }

    /// <summary>The Advanced Query ({6.EX.'x'}): a trigger evaluates the parsed tree in memory, Search Records sends the same tree to SQL
    /// (and its in-memory half for an encrypted app). Every stored type, operator and value — a blank one too — must pick the same records.</summary>
    [Fact]
    public async Task AdvancedQuery_EveryTypeOperatorAndValue_TriggerSqlAndInMemory_PickTheSameRecords()
    {
        var conn = Environment.GetEnvironmentVariable("PB_PARITY_SQL");
        if (string.IsNullOrWhiteSpace(conn)) return;
        var fields = BuildFields();
        var lookup = fields.ToDictionary(f => (long)f.Fid!.Value);
        await using var db = new SqlConnection(conn);
        await db.OpenAsync();
        var rows = await CreateTableAsync(db, fields);
        var report = new StringBuilder();
        int mismatches = 0, checkedCount = 0;

        for (var i = 0; i < Cases.Length; i++)
        {
            var c = Cases[i];
            var fid = fields[i].Fid!.Value;
            var caseRows = rows.Where(r => r.CaseIndex == i).ToList();
            foreach (var op in AdvancedOperatorsFor(c))
                foreach (var operand in c.Operands.Append(""))
                {
                    // Text in order against a wildcard pattern ("A*"): punctuation sorts differently in SQL's collation and .NET's.
                    if (op is "GT" or "GTE" or "LT" or "LTE" && operand.IndexOfAny(['*', '?']) >= 0) continue;
                    var query = $"{{{fid}.{op}.'{operand.Replace("'", "\'")}'}}";
                    FilterGroup tree;
                    try { tree = CopyRecordsDefinition.ParseQuery(query, fields)!; }
                    // HAS / XHAS need a value to look for: the query is refused when saved, by design.
                    catch (PowerBase.Domain.Exceptions.ValidationException) when (op is "HAS" or "XHAS" && operand == "") { continue; }
                    catch (Exception ex) { report.AppendLine($"PARSE {c.TypeCode} {query}: {ex.Message}"); mismatches++; continue; }
                    // As the call sites read it: Search Records / Copy Records, and a trigger's in-memory filter.
                    var searchTree = PipelineAdvancedQuery.ForSearch(tree, fields);
                    var triggerTree = PipelineAdvancedQuery.ForTrigger(tree);

                    object? TriggerValue(object? v) => v is RangeValue rv ? rv.AsJson() : v;
                    var trigger = caseRows.Where(r => PipelineFilterEvaluator.EvaluateFilterGroup(triggerTree, new Dictionary<long, object?> { [fid] = TriggerValue(r.Value) }, fields))
                        .Select(r => r.Id).ToHashSet();
                    var parameters = new DynamicParameters();
                    var where = (string)BuildWhere.Invoke(null, [searchTree, parameters, lookup, null])!;
                    HashSet<long> sql;
                    try { sql = (await db.QueryAsync<long>($"SELECT Id FROM #pb WHERE IsDeleted = 0 AND Id IN ({string.Join(",", caseRows.Select(r => r.Id))}){where}", parameters)).ToHashSet(); }
                    catch (SqlException ex) { report.AppendLine($"SQL-ERROR {c.TypeCode} {query}: {ex.Message}"); mismatches++; continue; }
                    var memory = PipelineComputedFilter.Apply(
                            caseRows.Select(r => ((IReadOnlyDictionary<string, object?>)(r.Value is RangeValue mr
                                ? new Dictionary<string, object?> { ["Id"] = r.Id, [$"f_{fid}"] = mr.Start, [$"f_{fid}_e"] = mr.End }
                                : new Dictionary<string, object?> { ["Id"] = r.Id, [$"f_{fid}"] = r.Value }),
                                (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>())), searchTree, fields)
                        .Select(p => (long)p.Row["Id"]!).ToHashSet();

                    if (PipelineFilterEvaluator.IsRichTextField(fields[i])) sql = memory;
                    checkedCount++;
                    if (trigger.SetEquals(sql) && memory.SetEquals(sql)) continue;
                    mismatches++;
                    string Show(HashSet<long> set) => "[" + string.Join(", ", caseRows.Where(r => set.Contains(r.Id)).Select(r => Fmt(r.Value))) + "]";
                    report.AppendLine($"{c.TypeCode,-14} {query,-28}: sql={Show(sql)} memory={Show(memory)} trigger={Show(trigger)}");
                }
        }

        _output.WriteLine($"checked {checkedCount}, mismatches {mismatches}");
        var outFile = Environment.GetEnvironmentVariable("PB_PARITY_OUT");
        if (!string.IsNullOrWhiteSpace(outFile)) await File.WriteAllTextAsync(outFile + ".aq.txt", $"checked {checkedCount}, mismatches {mismatches}\n{report}");
        Assert.True(mismatches == 0, $"checked {checkedCount}, mismatches {mismatches}\n{report}");
    }

    internal static string Fmt(object? v) => v switch
    {
        null => "null",
        RangeValue r => $"{Fmt(r.Start)}..{Fmt(r.End)}",
        string s => $"\"{s}\"",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}
