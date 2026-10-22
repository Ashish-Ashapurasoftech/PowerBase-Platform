using System.Reflection;
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

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// "is empty" / "is not empty" on a list (a formula returning choices or users, a Multi-select): a list with no items reads as "[]",
/// and is empty — in a trigger's filter and the Condition step as in Search Records. A text field holding "[]" is still not empty.
/// </summary>
public class PipelineFilterEmptyListTests
{
    private static readonly List<AppField> Fields = new()
    {
        new() { Id = 301, Fid = 40, Name = "F multiselect", TypeCode = "Formula_MultiSelect" },
        new() { Id = 302, Fid = 41, Name = "F users", TypeCode = "Formula_ListUser" },
        new() { Id = 303, Fid = 42, Name = "Multi", TypeCode = "MultiSelect", PhysicalColumnName = "f_42" },
        new() { Id = 304, Fid = 20, Name = "Title", TypeCode = "Text", PhysicalColumnName = "f_20" },
    };

    /// <summary>R4's formula returns no items; R5 has no value at all.</summary>
    private static readonly Dictionary<string, object?> Lists = new()
    {
        ["R1"] = new List<string> { "Red" },
        ["R2"] = new List<string> { "Red", "Blue" },
        ["R3"] = new List<string> { "Green" },
        ["R4"] = new List<string>(),
        ["R5"] = null,
    };

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "is-empty", "R4,R5" },
        new object[] { "is-not-empty", "R1,R2,R3" },
    };

    private static TriggerFilterGroup Group(string field, string op) =>
        new() { LogicalOp = "AND", Rules = new() { new TriggerFilterRule { Field = field, Operator = op, Value = "" } } };

    private static bool Trigger(string field, string op, object? stored, int fid) =>
        PipelineFilterEvaluator.EvaluateGroup(Group(field, op), new Dictionary<long, object?> { [fid] = stored }, Fields);

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
    private static FilterGroup Map(string field, string op)
    {
        var map = typeof(PipelineEngine).GetMethod("MapTriggerFilterGroupToDbFilterGroup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (FilterGroup)map.Invoke(Engine(), new object?[] { Group(field, op), Fields, "{}", "path", new List<PipelineStep>(), null })!;
    }

    private static bool InMemory(FilterGroup tree, IReadOnlyDictionary<long, object?> computed) =>
        PipelineComputedFilter.Apply(new[] { ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(), computed) }, tree, Fields).Count == 1;

    private static async Task<bool> Condition(string leftToken, string left, string op)
    {
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid() };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(Fields);
        var steps = new List<PipelineStep> { new() { Id = 1, RefId = "trigger", Type = "trigger", Subtype = "new-event", ConfigJson = $"{{\"tableId\":\"{table.PublicId}\"}}" } };
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateConditionRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<bool>)evaluate.Invoke(Engine(fieldRepo, tableRepo), new object?[]
        {
            leftToken, left, op, "", "TEXT", "{}", steps, fieldRepo, tableRepo, CancellationToken.None
        })!;
    }

    private static string Keys(IEnumerable<string> keys) => string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Trigger_AnEmptyListIsEmpty(string op, string expected)
    {
        foreach (var fid in new[] { 40, 41, 42 })
            Assert.Equal(expected, Keys(Lists.Where(r => Trigger($"fid_{fid}", op, r.Value, fid)).Select(r => r.Key)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ConditionStep_AnEmptyListIsEmpty(string op, string expected)
    {
        // The Condition step offers "is_blank" / "is_not_blank"; a list token resolves to its JSON array.
        var conditionOp = op == "is-empty" ? "is_blank" : "is_not_blank";
        foreach (var fid in new[] { 40, 41, 42 })
        {
            var matched = new List<string>();
            foreach (var (key, stored) in Lists)
                if (await Condition($"{{{{trigger.fid_{fid}}}}}", PipelineFilterEvaluator.ValueText(stored), conditionOp)) matched.Add(key);
            Assert.Equal(expected, Keys(matched));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SearchRecords_InMemory_AnEmptyListIsEmpty(string op, string expected)
    {
        var tree = Map("fid_40", op);
        Assert.Equal(expected, Keys(Lists.Where(r => InMemory(tree, new Dictionary<long, object?> { [40] = r.Value })).Select(r => r.Key)));
    }

    [Fact]
    public async Task TextHoldingBrackets_IsStillNotEmpty()
    {
        Assert.True(Trigger("fid_20", "is-not-empty", "[]", 20));
        Assert.False(Trigger("fid_20", "is-empty", "[]", 20));
        Assert.True(await Condition("{{trigger.fid_20}}", "[]", "is_not_blank"));
        Assert.False(await Condition("{{trigger.fid_20}}", "[]", "is_blank"));
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("", true)]
    [InlineData("[\"Red\"]", false)]
    [InlineData("Red;Blue", false)]
    public void EvaluateListBlank_ReadsTheItems(string value, bool blank)
    {
        Assert.Equal(blank, PipelineFilterEvaluator.EvaluateListBlank(value, "is_blank"));
        Assert.Equal(!blank, PipelineFilterEvaluator.EvaluateListBlank(value, "is-not-empty"));
        Assert.Null(PipelineFilterEvaluator.EvaluateListBlank(value, "contains"));
    }
}
