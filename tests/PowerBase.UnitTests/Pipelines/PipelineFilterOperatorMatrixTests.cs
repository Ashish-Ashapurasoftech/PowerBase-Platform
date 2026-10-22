using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Every field type × every operator a PowerFlow filter offers, from the list the UI's tests check too
/// (Fixtures/pipeline-filter-operator-matrix.json): each one must save (the validator), run in a trigger's filter, become SQL (or the
/// in-memory check of a computed field) in Search Records, and run in the Condition step — and no path may meet an operator it does not
/// know (an unknown operator silently matches nothing, or everything).
/// </summary>
public class PipelineFilterOperatorMatrixTests
{
    public sealed record Entry(string Label, string TypeCode, string? Settings, int Fid, string Field, string Category, string Group,
        string[] Editor, string[]? Condition);

    private static readonly List<Entry> Matrix = LoadMatrix();

    private static List<Entry> LoadMatrix([CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "pipeline-filter-operator-matrix.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("entries").Deserialize<List<Entry>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static readonly List<AppField> Fields = Matrix.Where(e => e.Condition != null).Select(e => new AppField
    {
        Id = 1000 + e.Fid, Fid = e.Fid, Name = $"name_{e.Fid}", Label = e.Label, TypeCode = e.TypeCode, Settings = e.Settings,
        PhysicalColumnName = PhysicalNaming.IsComputedTypeCode(e.TypeCode) ? null : $"f_{e.Fid}"
    }).ToList();

    private static readonly Dictionary<long, PipelineUserDetails> People = new()
    {
        [40021] = new(40021, "Dipak", "Patel", "dipak@example.com", "dipak.p", "Dipak Patel"),
        [40022] = new(40022, "Ronak", "Dhamsaniya", "ronak@example.com", "ronak.d", "Ronak Dhamsaniya"),
    };

    /// <summary>The operators the SQL filter (RecordRepository) and the in-memory search (PipelineComputedFilter) know.</summary>
    private static readonly HashSet<string> SearchOperators = new()
    {
        "eq", "ne", "gt", "gte", "lt", "lte", "contains", "notContains", "startsWith", "notStartsWith", "endsWith", "notEndsWith",
        "wildcard", "notWildcard", "includes", "notIncludes", "in", "notIn", "isEmpty", "isNotEmpty",
        "date_eq", "date_ne", "date_gt", "date_gte", "date_lt", "date_lte"
    };

    private const string Period = "@relative-period|previous|2|month|Asia/Kolkata";

    private static string Sample(Entry entry, string op) => op switch
    {
        "is-empty" or "is-not-empty" or "is_blank" or "is_not_blank" or "is_true" or "is_false" or "is-current-user" or "is-not-current-user" => "",
        "during" or "not-during" => Period,
        "includes" or "not-includes" => entry.Group == "userList" ? "40021, 40022" : "Red, Blue",
        "wildcard" or "not-wildcard" => "a*",
        _ => entry.Category switch { "NUMBER" => "12", "DATE" => "2026-10-15T00:00:00.000Z", "TIME" => "10:30:00", "BOOLEAN" => "true", _ => "abc" }
    };

    /// <summary>Values a record may hold for the field: blank, and one of its own type (the last is the one an in-memory search reads).</summary>
    private static IEnumerable<object?> Stored(Entry entry) => new object?[]
    {
        null, "",
        entry.TypeCode switch
        {
            "NumericRange" => "{\"start\":5,\"end\":20}",
            "DateRange" => "{\"start\":\"2026-10-01\",\"end\":\"2026-10-31\"}",
            _ => entry.Category switch
            {
                "NUMBER" => "12", "DATE" => DateTime.UtcNow.AddDays(-40).ToString("O"), "TIME" => "10:30:00", "BOOLEAN" => "true",
                _ when entry.Group is "user" or "userList" => "40021",
                _ when entry.Group == "multiSelect" => "Red;Blue",
                _ when entry.Group == "file" => "[{\"name\":\"abc.pdf\",\"path\":\"/files/abc.pdf\"}]",
                _ when entry.TypeCode == "Address" => "{\"city\":\"Pune\",\"zip\":\"411001\"}",
                _ => "abc"
            }
        }
    };

    public static IEnumerable<object[]> EditorCases() => Matrix.SelectMany(e => e.Editor.Select(op => new object[] { e.Label, op }));
    public static IEnumerable<object[]> ConditionCases() => Matrix.Where(e => e.Condition != null).SelectMany(e => e.Condition!.Select(op => new object[] { e.Label, op }));

    private static Entry EntryOf(string label) => Matrix.Single(e => e.Label == label);

    private static TriggerFilterGroup Group(string field, string op, string value) =>
        new() { LogicalOp = "AND", Rules = new() { new TriggerFilterRule { Field = field, Operator = op, Value = value } } };

    /// <summary>Collects every warning a filter logs (an unknown operator, a field it cannot find…).</summary>
    private sealed class WarningLog : ILogger
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void TheListCoversEveryOperatorTheBackendAllows_ForEveryType()
    {
        Assert.True(Matrix.Count >= 70);
        // Every operator the UI offers is one the save validator knows for that type's category.
        foreach (var entry in Matrix)
        {
            var category = entry.Field.Contains('.') ? "TEXT" : PipelineFilterEvaluator.GetTypeCategory(Fields.Single(f => f.Fid == entry.Fid));
            foreach (var op in entry.Editor)
                Assert.True(PipelineFilterEvaluator.AllowedOperatorsByTypeCategory[category].Contains(op), $"{entry.Label}: '{op}' not allowed for {category}");
        }
    }

    [Theory]
    [MemberData(nameof(EditorCases))]
    public void Trigger_EveryTypeAndOperator_SavesAndRuns(string label, string op)
    {
        var entry = EntryOf(label);
        var group = Group(entry.Field, op, Sample(entry, op));

        var errors = new Dictionary<string, List<string>>();
        PipelineFilterEvaluator.ValidateGroup(group, Fields, errors, "FilterGroups[0]");
        Assert.Empty(errors);

        foreach (var stored in Stored(entry))
        {
            var log = new WarningLog();
            var values = new Dictionary<long, object?> { [entry.Fid] = stored };
            var first = PipelineFilterEvaluator.EvaluateGroup(group, values, Fields, log, People, 40021);
            Assert.Empty(log.Warnings);
            // The same record gives the same answer every time.
            Assert.Equal(first, PipelineFilterEvaluator.EvaluateGroup(group, values, Fields, log, People, 40021));
        }
    }

    [Theory]
    [MemberData(nameof(EditorCases))]
    public void SearchRecords_EveryTypeAndOperator_BecomesAConditionTheSearchKnows(string label, string op)
    {
        var entry = EntryOf(label);
        var group = Group(entry.Field, op, Sample(entry, op));
        FilterGroup? tree;
        if (PipelineUserFieldFilter.TryResolveRule(group.Rules![0], Fields, out var userField, out var detail) && !PipelineUserFieldFilter.IsComputedUser(userField))
        {
            // A rule on a person (a detail, or a stored user field's "wildcard match") is worked out over the people the column holds
            // before the search runs (ResolveUserDetailFilterRulesAsync → BuildDbFilter); the search gets that condition.
            var node = PipelineUserFieldFilter.BuildDbFilter(userField, detail, op, Sample(entry, op), People.Keys.ToList(), People).Node;
            tree = new FilterGroup { Logic = "and", Nodes = node == null ? new() : new() { node } };
            if (node == null) return; // every record passes the rule: nothing to search on
        }
        else
        {
            var map = typeof(PipelineEngine).GetMethod("MapTriggerFilterGroupToDbFilterGroup", BindingFlags.NonPublic | BindingFlags.Instance)!;
            tree = (FilterGroup?)map.Invoke(Engine(), new object?[] { group, Fields, "{}", "path", new List<PipelineStep>(), null });
        }

        Assert.NotNull(tree);
        var conditions = Flatten(tree!).ToList();
        Assert.NotEmpty(conditions);
        Assert.All(conditions, c => Assert.Equal(entry.Fid, c.FieldId));
        Assert.All(conditions, c => Assert.True(SearchOperators.Contains(c.Operator), $"{label} '{op}' reached the search as '{c.Operator}'"));
        if (entry.Field.Contains('.') && entry.TypeCode == "Address")
            Assert.All(conditions, c => Assert.Equal(entry.Field.Split('.')[1], c.SubField));

        var field = Fields.Single(f => f.Fid == entry.Fid);
        if (PhysicalNaming.IsComputedTypeCode(field.TypeCode))
        {
            // No column: the condition is checked in memory, on each page of the search.
            var row = new Dictionary<string, object?>();
            var computed = new Dictionary<long, object?> { [entry.Fid] = Stored(entry).Last() };
            var pairs = new[] { ((IReadOnlyDictionary<string, object?>)row, (IReadOnlyDictionary<long, object?>)computed) };
            PipelineComputedFilter.Apply(pairs, tree!, Fields);
        }
        else
        {
            var parameters = new DynamicParameters();
            var lookup = Fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
            var sql = (string)typeof(RecordRepository).GetMethod("BuildFilterTreeWhere", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object?[] { tree, parameters, lookup, null })!;
            Assert.False(string.IsNullOrWhiteSpace(sql), $"{label} '{op}' gave no SQL");
            Assert.Equal(CountOf(sql, '('), CountOf(sql, ')'));
        }
    }

    [Theory]
    [MemberData(nameof(ConditionCases))]
    public void ConditionStep_EveryTypeAndOperator_Runs(string label, string op)
    {
        var entry = EntryOf(label);
        var field = Fields.Single(f => f.Fid == entry.Fid);
        var logger = Substitute.For<ILogger<PipelineEngine>>();
        var engine = Engine(logger);
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateConditionRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var stored in Stored(entry))
        {
            ((Task<bool>)evaluate.Invoke(engine, new object?[]
            {
                null, stored?.ToString() ?? string.Empty, op, Sample(entry, op), PipelineFilterEvaluator.GetTypeCategory(field), "{}",
                new List<PipelineStep>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IAppTableRepository>(), CancellationToken.None
            })!).GetAwaiter().GetResult();
        }
        var warnings = logger.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "Log" && (LogLevel)c.GetArguments()[0]! >= LogLevel.Warning).ToList();
        Assert.True(warnings.Count == 0, $"{label} '{op}' logged a warning");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private static int CountOf(string text, char c) => text.Count(x => x == c);

    private static IEnumerable<FilterCondition> Flatten(FilterGroup group) =>
        group.Nodes.SelectMany(n => n.Condition != null ? new[] { n.Condition } : Flatten(n.Group!));

    private static PipelineEngine Engine(ILogger<PipelineEngine>? logger = null)
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(40021L);
        return new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), logger ?? Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), queryContext,
            Substitute.For<IServiceScopeFactory>(), serviceProvider, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
    }

    // ── a formula / lookup user field in Search Records: "wildcard match" over the app's users ─────────────────────

    [Theory]
    [InlineData("Formula_User", null, "wildcard", "in")]
    [InlineData("Formula_User", null, "not-wildcard", "notIn")]
    [InlineData("Formula_ListUser", null, "wildcard", "includes")]
    [InlineData("Formula_ListUser", null, "not-wildcard", "notIncludes")]
    [InlineData("Lookup", "{\"sourceTypeCode\":\"User\"}", "wildcard", "in")]
    [InlineData("Lookup", "{\"sourceTypeCode\":\"MultiUser\"}", "wildcard", "includes")]
    public async Task ComputedUserField_Wildcard_IsMatchedOverTheAppsUsers(string typeCode, string? settings, string op, string sqlOp)
    {
        var field = new AppField { Id = 1, Fid = 70, Name = "Owner", TypeCode = typeCode, Settings = settings };
        Assert.True(PipelineUserFieldFilter.IsComputedUser(field));
        Assert.True(PipelineUserFieldFilter.TryResolveRule(new TriggerFilterRule { Field = "fid_70", Operator = op, Value = "ronak*" }, new[] { field }, out _, out var detail));
        Assert.Equal(PipelineUserFieldFilter.PersonDetail, detail);

        var dipak = Guid.NewGuid();
        var ronak = Guid.NewGuid();
        var appUsers = Substitute.For<IAppUserRepository>();
        appUsers.ListByAppIdAsync(5, Arg.Any<CancellationToken>()).Returns(new List<AppUserDetail>
        {
            new(Guid.NewGuid(), dipak, "Dipak Patel", "dipak@example.com", Guid.NewGuid(), "User", "Active", true, DateTime.UtcNow, false, false, null),
            new(Guid.NewGuid(), ronak, "Ronak Dhamsaniya", "ronak@example.com", Guid.NewGuid(), "User", "Active", true, DateTime.UtcNow, false, false, null),
        });
        var users = Substitute.For<IUserRepository>();
        users.GetByPublicIdAsync(dipak, Arg.Any<CancellationToken>()).Returns(new User { Id = 40021, FirstName = "Dipak", LastName = "Patel", Email = "dipak@example.com", Name = "Dipak Patel" });
        users.GetByPublicIdAsync(ronak, Arg.Any<CancellationToken>()).Returns(new User { Id = 40022, FirstName = "Ronak", LastName = "Dhamsaniya", Email = "ronak@example.com", Name = "Ronak Dhamsaniya" });

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        serviceProvider.GetService(typeof(IAppUserRepository)).Returns(appUsers);
        serviceProvider.GetService(typeof(IUserRepository)).Returns(users);
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<IServiceScopeFactory>(), serviceProvider, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());

        var build = typeof(PipelineEngine).GetMethod("FormulaUserPersonConditionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var node = await (Task<FilterNode>)build.Invoke(engine, new object?[] { field, new AppTable { AppId = 5 }, op, "ronak*", CancellationToken.None })!;

        Assert.Equal(sqlOp, node.Condition!.Operator);
        Assert.Contains("40022", node.Condition.Value);
        Assert.DoesNotContain("40021", node.Condition.Value);

        // Checked in memory on the formula's value (the ids it returns).
        var isList = sqlOp is "includes" or "notIncludes";
        bool Matches(string stored) => PipelineComputedFilter.Apply(
            new[] { ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(), (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [70] = stored }) },
            new FilterGroup { Logic = "and", Nodes = new() { node } }, new[] { field }).Count == 1;
        var positive = !op.StartsWith("not");
        Assert.Equal(positive, Matches(isList ? "[\"40021\",\"40022\"]" : "40022"));
        Assert.Equal(!positive, Matches(isList ? "[\"40021\"]" : "40021"));
    }

    [Fact]
    public async Task ComputedUserField_Wildcard_FindsTenantUsersWhoAreNotAppMembers()
    {
        // hardik is in the tenant (and so may be in the field) but not a member of this app.
        var field = new AppField { Id = 1, Fid = 70, Name = "Owner", TypeCode = "Formula_User" };
        var hardik = Guid.NewGuid();
        var tenants = Substitute.For<ITenantRepository>();
        tenants.ListUsersAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<PowerBase.Application.Users.TenantUserDetail> { new(hardik, "hardik963@mailinator.com", "hardik Patel", null, null, false, true, DateTime.UtcNow) });
        var appUsers = Substitute.For<IAppUserRepository>();
        appUsers.ListByAppIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<AppUserDetail>());
        var users = Substitute.For<IUserRepository>();
        users.GetByPublicIdAsync(hardik, Arg.Any<CancellationToken>()).Returns(new User { Id = 40020, FirstName = "hardik", LastName = "Patel", Email = "hardik963@mailinator.com", Name = "hardik Patel" });
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenants);
        serviceProvider.GetService(typeof(IAppUserRepository)).Returns(appUsers);
        serviceProvider.GetService(typeof(IUserRepository)).Returns(users);
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<IServiceScopeFactory>(), serviceProvider, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>());
        var build = typeof(PipelineEngine).GetMethod("FormulaUserPersonConditionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var byName = await (Task<FilterNode>)build.Invoke(engine, new object?[] { field, new AppTable { AppId = 5 }, "wildcard", "hardik ?atel", CancellationToken.None })!;
        Assert.Equal(("in", "[\"40020\"]"), (byName.Condition!.Operator, byName.Condition.Value));
        var byEmail = await (Task<FilterNode>)build.Invoke(engine, new object?[] { field, new AppTable { AppId = 5 }, "wildcard", "*@mailinator.com", CancellationToken.None })!;
        Assert.Contains("40020", byEmail.Condition!.Value);
    }

    [Fact]
    public void Trigger_AListFormulaValue_IsReadAsItsItems()
    {
        // A formula returning a list hands the trigger filter a list (user ids, or choices), not text.
        var users = new AppField { Id = 1, Fid = 72, Name = "F user list", TypeCode = "Formula_ListUser" };
        var choices = new AppField { Id = 2, Fid = 73, Name = "F multiselect", TypeCode = "Formula_MultiSelect" };
        var fields = new[] { users, choices };
        var values = new Dictionary<long, object?> { [72] = new List<long> { 40021, 40020 }, [73] = new List<string> { "Red", "Blue" } };
        bool Run(string field, string op, string value, long? current = null) =>
            PipelineFilterEvaluator.EvaluateGroup(Group(field, op, value), values, fields, null, People, current);

        Assert.True(Run("fid_72", "includes", "40020"));
        Assert.False(Run("fid_72", "not-includes", "40020"));
        Assert.True(Run("fid_72", "is-current-user", "", 40021));
        Assert.False(Run("fid_72", "is-not-current-user", "", 40021));
        Assert.True(Run("fid_72", "wildcard", "dipak*"));
        Assert.False(Run("fid_72", "wildcard", "nobody*"));
        Assert.True(Run("fid_73", "includes", "Blue"));
        Assert.True(Run("fid_73", "not-includes", "Green"));
        Assert.True(Run("fid_73", "contains", "Red"));
        Assert.Equal("[\"40021\",\"40020\"]", PipelineFilterEvaluator.ValueText(new List<long> { 40021, 40020 }));
        Assert.Equal("abc", PipelineFilterEvaluator.ValueText("abc"));
        Assert.Equal("12", PipelineFilterEvaluator.ValueText(12));
        Assert.Equal(string.Empty, PipelineFilterEvaluator.ValueText(null));
        Assert.Equal("{\"start\":1}", PipelineFilterEvaluator.ValueText(new Dictionary<string, int> { ["start"] = 1 }));
    }

    [Theory]
    [InlineData("MultiSelect", null, "[\"Red\"]", "Red", true)]
    [InlineData("MultiSelect", null, "[\"Red\",\"Blue\"]", "Blue, Red", true)]
    [InlineData("MultiSelect", null, "[\"Red\",\"Blue\"]", "Red", false)]
    [InlineData("MultiSelect", null, "[\"Red\"]", "[\"Red\"]", true)]
    [InlineData("MultiSelect", null, "Red;Blue", "red, blue", true)]
    [InlineData("MultiSelect", null, null, "Red", false)]
    [InlineData("MultiUser", null, "[40021]", "40021", true)]
    [InlineData("MultiUser", null, "[40021,40020]", "40021", false)]
    [InlineData("Lookup", "{\"sourceTypeCode\":\"MultiSelect\"}", "[\"Red\"]", "Red", true)]
    public void Trigger_IsOnAList_ComparesTheItems(string typeCode, string? settings, string? stored, string value, bool same)
    {
        // As Search Records and the report filter do: the same items, in any order — not the stored JSON text.
        var field = new AppField { Id = 1, Fid = 74, Name = "List", TypeCode = typeCode, Settings = settings };
        var values = new Dictionary<long, object?> { [74] = stored };
        Assert.Equal(same, PipelineFilterEvaluator.EvaluateGroup(Group("fid_74", "is", value), values, new[] { field }));
        Assert.Equal(!same, PipelineFilterEvaluator.EvaluateGroup(Group("fid_74", "is-not", value), values, new[] { field }));
    }

    [Fact]
    public void Trigger_IsOnAPlainTextField_IsUnchanged()
    {
        var field = new AppField { Id = 1, Fid = 75, Name = "Text", TypeCode = "Text" };
        var values = new Dictionary<long, object?> { [75] = "Red, Blue" };
        Assert.False(PipelineFilterEvaluator.EvaluateGroup(Group("fid_75", "is", "Blue, Red"), values, new[] { field }));
        Assert.True(PipelineFilterEvaluator.EvaluateGroup(Group("fid_75", "is", "red, blue"), values, new[] { field }));
    }

    [Fact]
    public void InMemorySearch_AListFormulaValue_IsReadAsItsItems()
    {
        var field = new AppField { Id = 1, Fid = 76, Name = "F multiselect", TypeCode = "Formula_MultiSelect" };
        bool Matches(object? stored, string op, string value) => PipelineComputedFilter.Apply(
            new[] { ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(), (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [76] = stored }) },
            new FilterGroup { Logic = "and", Nodes = new() { new FilterNode { Condition = new FilterCondition { FieldId = 76, Operator = op, Value = value } } } },
            new[] { field }).Count == 1;
        var list = new List<string> { "Red", "Blue" };
        Assert.True(Matches(list, "contains", "ed"));
        Assert.False(Matches(list, "notContains", "ed"));
        Assert.True(Matches(list, "includes", "Blue"));
        Assert.False(Matches(list, "contains", "Generic"));
        // "is" / "is not": the same items, in any order.
        Assert.True(Matches(list, "eq", "Blue, Red"));
        Assert.False(Matches(list, "eq", "Red"));
        Assert.True(Matches(list, "ne", "Red"));
        Assert.False(Matches(list, "ne", "Red, Blue"));
        // A lookup of a Multi-select hands over the stored JSON text.
        var lookup = new AppField { Id = 2, Fid = 77, Name = "L Multiple choice", TypeCode = "Lookup", Settings = "{\"sourceTypeCode\":\"MultiSelect\"}" };
        bool LookupMatches(object? stored, string op, string value) => PipelineComputedFilter.Apply(
            new[] { ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(), (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [77] = stored }) },
            new FilterGroup { Logic = "and", Nodes = new() { new FilterNode { Condition = new FilterCondition { FieldId = 77, Operator = op, Value = value } } } },
            new[] { lookup }).Count == 1;
        Assert.True(LookupMatches("[\"Red\"]", "eq", "Red"));
        Assert.False(LookupMatches("[\"Red\",\"Blue\"]", "eq", "Red"));
        // A plain text formula keeps comparing text.
        var text = new AppField { Id = 3, Fid = 78, Name = "F text", TypeCode = "Formula_Text" };
        Assert.Single(PipelineComputedFilter.Apply(
            new[] { ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(), (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [78] = "Red, Blue" }) },
            new FilterGroup { Logic = "and", Nodes = new() { new FilterNode { Condition = new FilterCondition { FieldId = 78, Operator = "eq", Value = "red, blue" } } } },
            new[] { text }));
    }

    [Fact]
    public void InMemorySearch_ABlankValueIsInNoOrder()
    {
        var field = new AppField { Id = 1, Fid = 71, Name = "F number", TypeCode = "Formula_Number" };
        bool Matches(object? stored, string op, string value) => PipelineComputedFilter.Apply(
            new[] { ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(), (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [71] = stored }) },
            new FilterGroup { Logic = "and", Nodes = new() { new FilterNode { Condition = new FilterCondition { FieldId = 71, Operator = op, Value = value } } } },
            new[] { field }).Count == 1;
        foreach (var op in new[] { "gt", "gte", "lt", "lte" })
        {
            Assert.False(Matches(null, op, "5"));
            Assert.False(Matches("", op, "5"));
        }
        Assert.True(Matches(3m, "lt", "5"));
        Assert.True(Matches(5m, "lte", "5"));
        Assert.True(Matches(7m, "gt", "5"));
        // Equality keeps its meaning: a blank is not 5.
        Assert.True(Matches(null, "ne", "5"));
        Assert.False(Matches(null, "eq", "5"));
    }

    [Fact]
    public void ComputedUserField_Wildcard_OnATrigger_MatchesThePerson()
    {
        var field = new AppField { Id = 1, Fid = 70, Name = "Owner", TypeCode = "Formula_User" };
        var group = Group("fid_70", "wildcard", "*@example.com");
        Assert.True(PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [70] = "40021" }, new[] { field }, null, People));
        Assert.False(PipelineFilterEvaluator.EvaluateGroup(Group("fid_70", "wildcard", "ronak*"), new Dictionary<long, object?> { [70] = "40021" }, new[] { field }, null, People));
        Assert.Single(PipelineUserFieldFilter.DetailRules(new[] { group }, new[] { field }));
    }

    [Fact]
    public void ALookupOfANonUserField_IsNotAUserField()
    {
        Assert.False(PipelineUserFieldFilter.IsComputedUser(new AppField { Id = 1, Fid = 70, Name = "L", TypeCode = "Lookup", Settings = "{\"sourceTypeCode\":\"Text\"}" }));
        Assert.False(PipelineUserFieldFilter.IsComputedUser(new AppField { Id = 1, Fid = 70, Name = "F", TypeCode = "Formula_Text" }));
        Assert.False(PipelineUserFieldFilter.IsComputedUserList(new AppField { Id = 1, Fid = 70, Name = "F", TypeCode = "Formula_User" }));
    }
}
