using System.Reflection;
using Dapper;
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

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// The four field types a live run of every PowerFlow filter operator found filtering wrongly — each now filters the same way in a
/// trigger's filter, the Condition step and Search Records (its SQL, and the in-memory search of a computed or encrypted field):
/// a File by its files' names, a time of day as a time, and a Date / Numeric Range as a whole range ("is" — the range includes the
/// value; "after" — it starts after it; "before" — it ends before it).
/// </summary>
public class PipelineFilterRangeTimeFileTests
{
    private static readonly List<AppField> Fields = new()
    {
        new() { Id = 201, Fid = 19, Name = "File", TypeCode = "File", PhysicalColumnName = "f_19" },
        new() { Id = 202, Fid = 51, Name = "L File", TypeCode = "Lookup", Settings = "{\"sourceTypeCode\":\"File\"}" },
        new() { Id = 203, Fid = 13, Name = "Time", TypeCode = "Time", PhysicalColumnName = "f_13" },
        new() { Id = 204, Fid = 32, Name = "F time", TypeCode = "Formula_Time" },
        new() { Id = 205, Fid = 54, Name = "L Time", TypeCode = "Lookup", Settings = "{\"sourceTypeCode\":\"Time\"}" },
        new() { Id = 206, Fid = 14, Name = "Date range", TypeCode = "DateRange", PhysicalColumnName = "f_14" },
        new() { Id = 207, Fid = 10, Name = "Numeric range", TypeCode = "NumericRange", PhysicalColumnName = "f_10" },
        new() { Id = 208, Fid = 20, Name = "Title", TypeCode = "Text", PhysicalColumnName = "f_20" },
    };

    private static readonly string[] Negated = { "is-not", "not-contains", "not-starts-with", "not-ends-with" };

    private static TriggerFilterGroup Group(string field, string op, string? value) =>
        new() { LogicalOp = "AND", Rules = new() { new TriggerFilterRule { Field = field, Operator = op, Value = value } } };

    private static bool Trigger(string field, string op, string? value, object? stored, int fid) =>
        PipelineFilterEvaluator.EvaluateGroup(Group(field, op, value), new Dictionary<long, object?> { [fid] = stored }, Fields);

    private static PipelineEngine Engine(IAppFieldRepository? fieldRepo = null, IAppTableRepository? tableRepo = null)
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        return new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            tableRepo ?? Substitute.For<IAppTableRepository>(), fieldRepo ?? Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<IServiceScopeFactory>(), serviceProvider, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    /// <summary>The Search Records filter a rule becomes.</summary>
    private static FilterGroup Map(string field, string op, string? value)
    {
        var map = typeof(PipelineEngine).GetMethod("MapTriggerFilterGroupToDbFilterGroup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (FilterGroup)map.Invoke(Engine(), new object?[] { Group(field, op, value), Fields, "{}", "path", new List<PipelineStep>(), null })!;
    }

    /// <summary>The SQL a Search Records filter runs, and its parameters.</summary>
    private static (string Sql, DynamicParameters Params) Sql(FilterGroup tree)
    {
        var parameters = new DynamicParameters();
        var lookup = Fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
        var sql = (string)typeof(RecordRepository).GetMethod("BuildFilterTreeWhere", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { tree, parameters, lookup, null })!;
        return (sql, parameters);
    }

    private static List<object?> ParameterValues(DynamicParameters parameters) =>
        parameters.ParameterNames.Select(name => parameters.Get<object?>(name)).ToList();

    /// <summary>Whether the in-memory search (a computed or encrypted field) keeps a record.</summary>
    private static bool InMemory(FilterGroup tree, IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<long, object?>? computed = null) =>
        PipelineComputedFilter.Apply(new[] { (row, computed ?? new Dictionary<long, object?>()) }, tree, Fields).Count == 1;

    private static Task<bool> Condition(string? leftToken, string left, string op, string right, string category,
        List<PipelineStep>? steps = null, IAppFieldRepository? fieldRepo = null, IAppTableRepository? tableRepo = null)
    {
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateConditionRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<bool>)evaluate.Invoke(Engine(fieldRepo, tableRepo), new object?[]
        {
            leftToken, left, op, right, category, "{}", steps ?? new List<PipelineStep>(),
            fieldRepo ?? Substitute.For<IAppFieldRepository>(), tableRepo ?? Substitute.For<IAppTableRepository>(), CancellationToken.None
        })!;
    }

    private static string Keys(IEnumerable<string> keys) => string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal));

    // ── File: the files' names ──────────────────────────────────────────────────────────────────────────────────────

    private static string FileJson(params string[] names) =>
        "[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\",\"path\":\"/files/test/{n}\",\"size\":10,\"type\":\"application/pdf\"}}")) + "]";

    private static readonly Dictionary<string, string?> Files = new()
    {
        ["R1"] = FileJson("invoice-2026.pdf"),
        ["R2"] = FileJson("bill_2025.docx"),
        ["R3"] = FileJson("Report%final.pdf"),
        ["R4"] = null,
        ["R5"] = FileJson("a.txt", "Invoice-old.PDF"),
        ["R6"] = "{\"name\":\"old.pdf\",\"path\":\"/files/old.pdf\"}",   // one file, saved before File held a list
    };

    public static IEnumerable<object[]> FileCases() => new[]
    {
        new object[] { "is", "INVOICE-2026.PDF", "R1" },
        new object[] { "is-not", "invoice-2026.pdf", "R2,R3,R4,R5,R6" },
        new object[] { "starts-with", "INV", "R1,R5" },
        new object[] { "not-starts-with", "inv", "R2,R3,R4,R6" },
        new object[] { "ends-with", "pdf", "R1,R3,R5,R6" },
        new object[] { "not-ends-with", "PDF", "R2,R4" },
        new object[] { "contains", "nvoi", "R1,R5" },
        new object[] { "not-contains", "nvoi", "R2,R3,R4,R6" },
        // The path and type are not the name: "files" and "application" are in every stored value, in no file's name.
        new object[] { "contains", "files", "" },
        new object[] { "contains", "application", "" },
        new object[] { "contains", "%f", "R3" },
        new object[] { "is-empty", "", "R4" },
        new object[] { "is-not-empty", "", "R1,R2,R3,R5,R6" },
    };

    [Theory]
    [MemberData(nameof(FileCases))]
    public void File_Trigger_ComparesTheFileNames(string op, string value, string expected)
    {
        Assert.Equal(expected, Keys(Files.Where(r => Trigger("fid_19", op, value, r.Value, 19)).Select(r => r.Key)));
        // A Lookup of a File the same.
        Assert.Equal(expected, Keys(Files.Where(r => Trigger("fid_51", op, value, r.Value, 51)).Select(r => r.Key)));
    }

    [Theory]
    [MemberData(nameof(FileCases))]
    public async Task File_ConditionStep_ComparesTheFileNames(string op, string value, string expected)
    {
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid() };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(Fields);
        var steps = new List<PipelineStep> { new() { Id = 1, RefId = "trigger", Type = "trigger", Subtype = "new-event", ConfigJson = $"{{\"tableId\":\"{table.PublicId}\"}}" } };
        // The Condition step offers "equals" / "not-equals" for "is" / "is not".
        var conditionOp = op switch { "is" => "equals", "is-not" => "not-equals", "is-empty" => "is_blank", "is-not-empty" => "is_not_blank", _ => op };

        var matched = new List<string>();
        foreach (var (key, stored) in Files)
            if (await Condition("{{trigger.fid_19}}", stored ?? string.Empty, conditionOp, value, "TEXT", steps, fieldRepo, tableRepo)) matched.Add(key);
        Assert.Equal(expected, Keys(matched));
    }

    [Theory]
    [MemberData(nameof(FileCases))]
    public void File_SearchRecords_InMemory_ComparesTheFileNames(string op, string value, string expected)
    {
        // A Lookup of a File (computed) is searched in memory.
        var tree = Map("fid_51", op, value);
        var row = new Dictionary<string, object?>();
        Assert.Equal(expected, Keys(Files.Where(r => InMemory(tree, row, new Dictionary<long, object?> { [51] = r.Value })).Select(r => r.Key)));
    }

    [Theory]
    [InlineData("eq", "fn.name) = @fv0", "invoice.pdf")]
    [InlineData("startsWith", "fn.name) LIKE @fv0", "inv%")]
    [InlineData("endsWith", "fn.name) LIKE @fv0", "%.pdf")]
    [InlineData("contains", "fn.name) LIKE @fv0", "%voi%")]
    [InlineData("wildcard", "fn.name) LIKE @fv0", "inv%")]
    public void File_Sql_ComparesEachFilesName(string op, string compare, string parameter)
    {
        var value = op switch { "eq" => "Invoice.PDF", "startsWith" => "Inv", "endsWith" => ".pdf", "contains" => "voi", _ => "Inv*" };
        var (sql, p) = Sql(new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 19, Operator = op, Value = value } }] });

        Assert.Contains("OPENJSON(CASE WHEN ISJSON(f_19) = 1 THEN f_19 END) WITH (name NVARCHAR(MAX) '$.name')", sql);
        Assert.Contains(compare, sql);
        // An old value that is not JSON (a link) is compared whole.
        Assert.Contains("ISJSON(f_19) = 0 AND LOWER(f_19)", sql);
        Assert.Equal(parameter, p.Get<string>("fv0"));
    }

    [Theory]
    [InlineData("ne")]
    [InlineData("notStartsWith")]
    [InlineData("notEndsWith")]
    [InlineData("notContains")]
    [InlineData("notWildcard")]
    public void File_Sql_ANegatedOperatorMeansNoFileNameMatches(string op)
    {
        var (sql, _) = Sql(new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 19, Operator = op, Value = "x" } }] });
        Assert.Contains("f_19 IS NOT NULL AND NOT (EXISTS (SELECT 1 FROM OPENJSON(", sql);
    }

    [Fact]
    public void File_Sql_BlankChecksStillLookAtTheValue()
    {
        var (sql, _) = Sql(new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 19, Operator = "isEmpty" } }] });
        Assert.DoesNotContain("OPENJSON", sql);
        Assert.Contains("f_19 IS NULL", sql);
    }

    [Fact]
    public void File_AdvancedQuery_OnATrigger_ComparesTheFileNames()
    {
        bool Run(string op, string value, string? stored) => PipelineFilterEvaluator.EvaluateFilterGroup(
            new FilterGroup { Logic = "and", Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 19, Operator = op, Value = value } }] },
            new Dictionary<long, object?> { [19] = stored }, Fields);
        Assert.True(Run("eq", "invoice-2026.pdf", Files["R1"]));
        Assert.True(Run("startsWith", "inv", Files["R1"]));
        Assert.False(Run("notStartsWith", "inv", Files["R1"]));
        Assert.True(Run("notStartsWith", "inv", Files["R2"]));
        Assert.True(Run("ne", "invoice-2026.pdf", null));
    }

    [Theory]
    [InlineData("[{\"name\":\"a.pdf\"},{\"name\":\"b.txt\"}]", "a.pdf|b.txt")]
    [InlineData("{\"name\":\"one.pdf\",\"path\":\"/x\"}", "one.pdf")]
    [InlineData("https://example.com/report.pdf", "https://example.com/report.pdf")]
    [InlineData("[draft] notes", "[draft] notes")]
    [InlineData("[]", "")]
    [InlineData("", "")]
    public void FileNames_ReadAsTheSqlFilterReadsThem(string stored, string names) =>
        Assert.Equal(names, string.Join("|", PipelineFilterEvaluator.FileNames(stored)));

    [Fact]
    public void File_APlainTextFieldIsNotReadAsFiles()
    {
        // Only a File field (or a Lookup of one) is read as files: a Text field holding the same JSON keeps comparing its text.
        Assert.False(Trigger("fid_20", "starts-with", "inv", Files["R1"], 20));
        Assert.True(Trigger("fid_20", "starts-with", "[{", Files["R1"], 20));
    }

    // ── Time: a time of day ─────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string?> Times = new()
    {
        ["R1"] = "10:30:00", ["R2"] = "18:45", ["R3"] = "00:15:00", ["R4"] = null, ["R5"] = "23:59:00", ["R6"] = "12:00:00",
    };

    public static IEnumerable<object[]> TimeCases() => new[]
    {
        new object[] { "is", "10:30:00", "R1" },
        new object[] { "is", "10:30", "R1" },
        new object[] { "is-not", "10:30:00", "R2,R3,R4,R5,R6" },
        new object[] { "is-after", "10:30:00", "R2,R5,R6" },
        new object[] { "is-on-or-after", "10:30:00", "R1,R2,R5,R6" },
        new object[] { "is-before", "10:30:00", "R3" },
        new object[] { "is-on-or-before", "10:30:00", "R1,R3" },
        new object[] { "is-empty", "", "R4" },
        new object[] { "is-not-empty", "", "R1,R2,R3,R5,R6" },
    };

    [Theory]
    [InlineData("Time", null)]
    [InlineData("Formula_Time", null)]
    [InlineData("Lookup", "{\"sourceTypeCode\":\"Time\"}")]
    public void Time_IsItsOwnCategory_NotADate(string typeCode, string? settings)
    {
        Assert.Equal("TIME", PipelineFilterEvaluator.GetTypeCategory(new AppField { TypeCode = typeCode, Settings = settings }));
        Assert.Equal("TIME", PipelineFilterEvaluator.GetTypeCategory("TIME_OF_DAY"));
        Assert.Equal("DATE", PipelineFilterEvaluator.GetTypeCategory("DateTime"));
    }

    [Theory]
    [MemberData(nameof(TimeCases))]
    public void Time_Trigger_ComparesTimes(string op, string value, string expected)
    {
        foreach (var fid in new[] { 13, 32, 54 })
            Assert.Equal(expected, Keys(Times.Where(r => Trigger($"fid_{fid}", op, value, r.Value, fid)).Select(r => r.Key)));
        // A Lookup hands over the parent's TIME column as a TimeSpan.
        Assert.Equal(expected, Keys(Times.Where(r => Trigger("fid_54", op, value, r.Value is null ? null : TimeSpan.Parse(r.Value), 54)).Select(r => r.Key)));
    }

    [Theory]
    [MemberData(nameof(TimeCases))]
    public async Task Time_ConditionStep_ComparesTimes(string op, string value, string expected)
    {
        var conditionOp = op switch
        {
            "is" => "equals", "is-not" => "not-equals", "is-after" => "after", "is-on-or-after" => "on-or-after", "is-before" => "before",
            "is-on-or-before" => "on-or-before", "is-empty" => "is_blank", _ => "is_not_blank"
        };
        var matched = new List<string>();
        foreach (var (key, stored) in Times)
            if (await Condition(null, stored ?? string.Empty, conditionOp, value, "TIME")) matched.Add(key);
        Assert.Equal(expected, Keys(matched));
    }

    [Theory]
    [MemberData(nameof(TimeCases))]
    public void Time_SearchRecords_InMemory_ComparesTimes(string op, string value, string expected)
    {
        // A time formula reads "HH:mm:ss"; a Lookup of a Time the parent's TimeSpan.
        var row = new Dictionary<string, object?>();
        Assert.Equal(expected, Keys(Times.Where(r => InMemory(Map("fid_32", op, value), row, new Dictionary<long, object?> { [32] = r.Value })).Select(r => r.Key)));
        Assert.Equal(expected, Keys(Times.Where(r => InMemory(Map("fid_54", op, value), row,
            new Dictionary<long, object?> { [54] = r.Value is null ? null : TimeSpan.Parse(r.Value) })).Select(r => r.Key)));
    }

    [Theory]
    [InlineData("is", "eq")]
    [InlineData("is-not", "ne")]
    [InlineData("is-after", "gt")]
    [InlineData("is-on-or-after", "gte")]
    [InlineData("is-before", "lt")]
    [InlineData("is-on-or-before", "lte")]
    public void Time_SearchRecords_ComparesTheTime_NotADay(string op, string sqlOp)
    {
        var tree = Map("fid_13", op, "9:30");
        var node = Assert.Single(tree.Nodes);
        var condition = SearchFilterNodes.WithoutBlank(node).Condition!;
        Assert.Equal((sqlOp, "09:30:00"), (condition.Operator, condition.Value));
        Assert.Equal(sqlOp == "ne", SearchFilterNodes.IsOrBlank(node));

        // A TIME parameter: never a date window (DATEADD), which SQL refuses to compare with a TIME column.
        var (sql, p) = Sql(tree);
        Assert.DoesNotContain("DATEADD", sql);
        Assert.Equal(new TimeSpan(9, 30, 0), Assert.Single(ParameterValues(p)));
    }

    [Theory]
    [InlineData("is", false)]
    [InlineData("is-after", false)]
    [InlineData("is-not", true)]
    public void Time_SearchRecords_AValueThatIsNoTime_NeverReachesSql(string op, bool everyTime)
    {
        var tree = Map("fid_13", op, "half past ten");
        var (sql, p) = Sql(tree);
        Assert.DoesNotContain("half past ten", ParameterValues(p).Select(v => v?.ToString()));
        if (everyTime) Assert.DoesNotContain("IS NULL AND", sql);
        Assert.Contains(everyTime ? "f_13 IS NOT NULL" : "f_13 IS NULL AND f_13 IS NOT NULL", sql);
        // The trigger filter agrees: nothing is that time, every time is not it.
        Assert.Equal(everyTime, Trigger("fid_13", op, "half past ten", "10:30:00", 13));
    }

    [Theory]
    [InlineData("10:30", 10, 30, 0)]
    [InlineData("9:05:07", 9, 5, 7)]
    [InlineData("23:59:59.5", 23, 59, 59)]
    [InlineData("10:30 PM", 22, 30, 0)]
    [InlineData("2026-10-19T10:30:00", 10, 30, 0)]
    public void TimeOfDay_ReadsTheUsualForms(string text, int h, int m, int s)
    {
        Assert.True(PipelineFilterEvaluator.TryParseTimeOfDay(text, out var time));
        Assert.Equal((h, m, s), (time.Hours, time.Minutes, time.Seconds));
    }

    [Theory]
    [InlineData("10")]
    [InlineData("1.10:30")]
    [InlineData("25:00")]
    [InlineData("abc")]
    [InlineData("")]
    public void TimeOfDay_RefusesWhatIsNoTimeOfDay(string text) => Assert.False(PipelineFilterEvaluator.TryParseTimeOfDay(text, out _));

    [Fact]
    public void Time_Validator_OffersNoPeriodOperators()
    {
        var errors = new Dictionary<string, List<string>>();
        PipelineFilterEvaluator.ValidateGroup(Group("fid_13", "is-after", "10:30:00"), Fields, errors, "FilterGroups[0]");
        Assert.Empty(errors);
        PipelineFilterEvaluator.ValidateGroup(Group("fid_13", "during", "@relative-period|current|1|week|UTC"), Fields, errors, "FilterGroups[0]");
        Assert.NotEmpty(errors);
    }

    // ── Numeric Range: the whole range ──────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, (decimal? Start, decimal? End)> NumberRanges = new()
    {
        ["R1"] = (1, 9), ["R2"] = (10, 20), ["R3"] = (5, null), ["R4"] = (null, null), ["R5"] = (null, 3), ["R6"] = (-2, 2),
    };

    private static string? RangeJson<T>((T? Start, T? End) range, Func<T, string> write) where T : struct =>
        range.Start is null && range.End is null ? null
        : $"{{\"start\":{(range.Start is { } s ? write(s) : "null")},\"end\":{(range.End is { } e ? write(e) : "null")}}}";

    private static string? NumberRangeJson((decimal? Start, decimal? End) range) =>
        RangeJson(range, n => n.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static IEnumerable<object[]> NumberRangeCases() => new[]
    {
        // A number: the range includes it / starts above it / ends below it. A range with one end is that number.
        new object[] { "is", "5", "R1,R3" },
        new object[] { "is-not", "5", "R2,R4,R5,R6" },
        new object[] { "greater_than", "5", "R2" },
        new object[] { "greater_than_or_equals", "5", "R2,R3" },
        new object[] { "less_than", "5", "R5,R6" },
        new object[] { "less_than_or_equals", "5", "R3,R5,R6" },
        // Another range ({"start":1,"end":9}, as a token from another record holds it).
        new object[] { "is", "{\"start\":1,\"end\":9}", "R1" },
        new object[] { "greater_than", "{\"start\":1,\"end\":9}", "R2" },
        new object[] { "greater_than_or_equals", "{\"START\":1,\"END\":9}", "R1,R2,R3,R5" },
        new object[] { "less_than", "{\"start\":1,\"end\":9}", "" },
        new object[] { "less_than_or_equals", "{\"start\":1,\"end\":9}", "R1,R3,R5,R6" },
        new object[] { "is-empty", "", "R4" },
        new object[] { "is-not-empty", "", "R1,R2,R3,R5,R6" },
    };

    [Theory]
    [MemberData(nameof(NumberRangeCases))]
    public void NumericRange_Trigger_ComparesTheWholeRange(string op, string value, string expected)
    {
        Assert.Equal(expected, Keys(NumberRanges.Where(r => Trigger("fid_10", op, value, NumberRangeJson(r.Value), 10)).Select(r => r.Key)));
        // The trigger hands over the written value; as a { start, end } object it reads the same.
        Assert.Equal(expected, Keys(NumberRanges.Where(r => Trigger("fid_10", op, value,
            r.Value is (null, null) ? null : new Dictionary<string, object?> { ["start"] = r.Value.Start, ["end"] = r.Value.End }, 10)).Select(r => r.Key)));
    }

    [Theory]
    [MemberData(nameof(NumberRangeCases))]
    public async Task NumericRange_ConditionStep_ComparesTheWholeRange(string op, string value, string expected)
    {
        var conditionOp = op switch
        {
            "is" => "equals", "is-not" => "not-equals", "greater_than" => "greater-than", "greater_than_or_equals" => "greater-than-or-equal",
            "less_than" => "less-than", "less_than_or_equals" => "less-than-or-equal", "is-empty" => "is_blank", _ => "is_not_blank"
        };
        var matched = new List<string>();
        foreach (var (key, range) in NumberRanges)
            if (await Condition(null, NumberRangeJson(range) ?? string.Empty, conditionOp, value, "NUMBER")) matched.Add(key);
        Assert.Equal(expected, Keys(matched));
    }

    [Theory]
    [MemberData(nameof(NumberRangeCases))]
    public void NumericRange_SearchRecords_AgreesWithTheTrigger(string op, string value, string expected)
    {
        var tree = Map("fid_10", op, value);
        Assert.All(Flatten(tree), c => Assert.Contains(c.SubField, new[] { "start", "end" }));
        // The checks SQL makes on the two columns, run here on the same values (and on an encrypted range, stored as JSON).
        var columns = Keys(NumberRanges.Where(r => InMemory(tree, new Dictionary<string, object?> { ["f_10"] = r.Value.Start, ["f_10_e"] = r.Value.End })).Select(r => r.Key));
        var encrypted = Keys(NumberRanges.Where(r => InMemory(tree, new Dictionary<string, object?> { ["f_10"] = NumberRangeJson(r.Value) })).Select(r => r.Key));
        // "is not" keeps a blank range, as the trigger does (it used to be left out by SQL's NOT on NULL).
        var sqlExpected = expected;
        Assert.Equal(sqlExpected, columns);
        Assert.Equal(sqlExpected, encrypted);

        var (sql, p) = Sql(tree);
        Assert.Equal(sql.Count(c => c == '('), sql.Count(c => c == ')'));
        Assert.All(ParameterValues(p), v => Assert.True(v is long or decimal, $"{v} ({v?.GetType().Name}) is not a number"));
    }

    [Fact]
    public void NumericRange_GreaterThan_IsTheSameInATriggerAndInSearchRecords()
    {
        // The live run: "greater than" found nothing in a trigger but R1 and R2 in Search Records (the range's start, compared as text).
        foreach (var value in new[] { "5", "{\"start\":1,\"end\":9}" })
        {
            var trigger = Keys(NumberRanges.Where(r => Trigger("fid_10", "greater_than", value, NumberRangeJson(r.Value), 10)).Select(r => r.Key));
            var search = Keys(NumberRanges.Where(r => InMemory(Map("fid_10", "greater_than", value), new Dictionary<string, object?> { ["f_10"] = r.Value.Start, ["f_10_e"] = r.Value.End })).Select(r => r.Key));
            Assert.Equal("R2", trigger);
            Assert.Equal(trigger, search);
        }
    }

    // ── Date Range: the whole range, a day being the whole day ─────────────────────────────────────────────────────

    private static readonly Dictionary<string, (DateTime? Start, DateTime? End)> DateRanges = new()
    {
        ["R1"] = (new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)),
        ["R2"] = (new DateTime(2026, 8, 1), new DateTime(2026, 8, 31)),
        ["R3"] = (new DateTime(2026, 11, 5), null),
        ["R4"] = (null, null),
        ["R5"] = (null, new DateTime(2026, 10, 14)),
        ["R6"] = (new DateTime(2026, 10, 14), new DateTime(2026, 10, 14)),
    };

    private static string? DateRangeJson((DateTime? Start, DateTime? End) range) => RangeJson(range, d => $"\"{d:yyyy-MM-dd}\"");

    public static IEnumerable<object[]> DateRangeCases()
    {
        // 14 Oct picked in India (the instant of its local midnight), and in UTC: the same answers.
        foreach (var day in new[] { "2026-10-13T18:30:00.000Z", "2026-10-14T00:00:00.000Z" })
        {
            yield return new object[] { "is", day, "R1,R5,R6" };
            yield return new object[] { "is-not", day, "R2,R3,R4" };
            yield return new object[] { "is-after", day, "R3" };
            yield return new object[] { "is-on-or-after", day, "R3,R5,R6" };
            yield return new object[] { "is-before", day, "R2" };
            yield return new object[] { "is-on-or-before", day, "R2,R5,R6" };
        }
        // Another range: 1–31 Oct.
        const string october = "{\"start\":\"2026-10-01T00:00:00\",\"end\":\"2026-10-31T00:00:00\"}";
        yield return new object[] { "is", october, "R1" };
        yield return new object[] { "is-not", october, "R2,R3,R4,R5,R6" };
        yield return new object[] { "is-after", october, "R3" };
        yield return new object[] { "is-on-or-after", october, "R1,R3,R5,R6" };
        yield return new object[] { "is-before", october, "R2" };
        yield return new object[] { "is-on-or-before", october, "R1,R2,R5,R6" };
        yield return new object[] { "is-empty", "", "R4" };
        yield return new object[] { "is-not-empty", "", "R1,R2,R3,R5,R6" };
    }

    [Theory]
    [MemberData(nameof(DateRangeCases))]
    public void DateRange_Trigger_ComparesTheWholeRange(string op, string value, string expected) =>
        Assert.Equal(expected, Keys(DateRanges.Where(r => Trigger("fid_14", op, value, DateRangeJson(r.Value), 14)).Select(r => r.Key)));

    [Theory]
    [MemberData(nameof(DateRangeCases))]
    public async Task DateRange_ConditionStep_ComparesTheWholeRange(string op, string value, string expected)
    {
        var conditionOp = op switch
        {
            "is" => "equals", "is-not" => "not-equals", "is-after" => "after", "is-on-or-after" => "on-or-after", "is-before" => "before",
            "is-on-or-before" => "on-or-before", "is-empty" => "is_blank", _ => "is_not_blank"
        };
        var matched = new List<string>();
        foreach (var (key, range) in DateRanges)
            if (await Condition(null, DateRangeJson(range) ?? string.Empty, conditionOp, value, "DATE")) matched.Add(key);
        Assert.Equal(expected, Keys(matched));
    }

    [Theory]
    [MemberData(nameof(DateRangeCases))]
    public void DateRange_SearchRecords_AgreesWithTheTrigger(string op, string value, string expected)
    {
        var tree = Map("fid_14", op, value);
        Assert.All(Flatten(tree), c => Assert.Contains(c.SubField, new[] { "start", "end" }));
        var columns = Keys(DateRanges.Where(r => InMemory(tree, new Dictionary<string, object?> { ["f_14"] = r.Value.Start, ["f_14_e"] = r.Value.End })).Select(r => r.Key));
        var encrypted = Keys(DateRanges.Where(r => InMemory(tree, new Dictionary<string, object?> { ["f_14"] = DateRangeJson(r.Value) })).Select(r => r.Key));
        // "is not" keeps a blank range, as the trigger does (it used to be left out by SQL's NOT on NULL).
        var sqlExpected = expected;
        Assert.Equal(sqlExpected, columns);
        Assert.Equal(sqlExpected, encrypted);

        // The range's DATE columns are compared with instants, never with the range's JSON text ("Conversion failed …").
        var (sql, p) = Sql(tree);
        Assert.Contains("f_14", sql);
        Assert.All(ParameterValues(p), v => Assert.IsType<DateTime>(v));
    }

    [Fact]
    public void DateRange_Today_IsTheWholeOfToday()
    {
        var zone = "UTC";
        var today = DateTime.UtcNow.Date;
        string Range(DateTime start, DateTime end) => $"{{\"start\":\"{start:yyyy-MM-dd}\",\"end\":\"{end:yyyy-MM-dd}\"}}";
        var value = $"@relative-date|today|0|{zone}";
        Assert.True(Trigger("fid_14", "is", value, Range(today.AddDays(-3), today.AddDays(3)), 14));
        Assert.False(Trigger("fid_14", "is", value, Range(today.AddDays(1), today.AddDays(3)), 14));
        Assert.True(Trigger("fid_14", "is-before", value, Range(today.AddDays(-9), today.AddDays(-1)), 14));
        var tree = Map("fid_14", "is", value);
        Assert.True(InMemory(tree, new Dictionary<string, object?> { ["f_14"] = today.AddDays(-3), ["f_14_e"] = today.AddDays(3) }));
        Assert.False(InMemory(tree, new Dictionary<string, object?> { ["f_14"] = today.AddDays(1), ["f_14_e"] = today.AddDays(3) }));
    }

    [Fact]
    public void DateRange_AValueThatIsNoDate_MatchesNothing()
    {
        Assert.False(Trigger("fid_14", "is", "soon", DateRangeJson(DateRanges["R1"]), 14));
        var (sql, p) = Sql(Map("fid_14", "is", "soon"));
        Assert.Empty(ParameterValues(p));
        Assert.Contains("IS NULL AND", sql);
    }

    private static IEnumerable<FilterCondition> Flatten(FilterGroup group) =>
        group.Nodes.SelectMany(n => n.Condition != null ? new[] { n.Condition } : Flatten(n.Group!));
}
