using System.Globalization;
using System.Reflection;
using System.Text;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// A Condition step compares a value an earlier step carries ({{trigger.fid_N}}, as text) with its operators ("equals", "after"…). For
/// every stored field type, every operator the Condition editor offers for it and a spread of values (a blank one too), it must answer
/// as the trigger filter does for the same rule.
/// </summary>
public class PipelineConditionParityTests
{
    private readonly ITestOutputHelper _output;
    public PipelineConditionParityTests(ITestOutputHelper output) => _output = output;

    /// <summary>The Condition editor's operators (pipeline-filter-helpers.ts CANONICAL_OPERATOR_OPTIONS and the extra groups), and the
    /// filter editor's word for the same rule.</summary>
    private static readonly Dictionary<string, (string Condition, string Filter)[]> Operators = new()
    {
        ["TEXT"] = [("equals", "is"), ("not-equals", "is-not"), ("contains", "contains"), ("not-contains", "not-contains"), ("starts-with", "starts-with"),
            ("ends-with", "ends-with"), ("is_blank", "is-empty"), ("is_not_blank", "is-not-empty")],
        ["NUMBER"] = [("equals", "is"), ("not-equals", "is-not"), ("greater-than", "greater_than"), ("greater-than-or-equal", "greater_than_or_equals"),
            ("less-than", "less_than"), ("less-than-or-equal", "less_than_or_equals"), ("is_blank", "is-empty"), ("is_not_blank", "is-not-empty")],
        ["DATE"] = [("equals", "is"), ("not-equals", "is-not"), ("after", "is-after"), ("on-or-after", "is-on-or-after"), ("before", "is-before"),
            ("on-or-before", "is-on-or-before"), ("is_blank", "is-empty"), ("is_not_blank", "is-not-empty")],
        ["TIME"] = [("equals", "is"), ("not-equals", "is-not"), ("after", "is-after"), ("on-or-after", "is-on-or-after"), ("before", "is-before"),
            ("on-or-before", "is-on-or-before"), ("is_blank", "is-empty"), ("is_not_blank", "is-not-empty")],
        ["BOOLEAN"] = [("is_true", "is_true"), ("is_false", "is_false"), ("equals", "is"), ("not-equals", "is-not")],
    };

    private static readonly Dictionary<string, (string Condition, string Filter)[]> Extra = new()
    {
        ["text"] = [("not-starts-with", "not-starts-with"), ("not-ends-with", "not-ends-with"), ("wildcard", "wildcard"), ("not-wildcard", "not-wildcard")],
        ["multiSelect"] = [("includes", "includes"), ("not-includes", "not-includes")],
        ["userList"] = [("includes", "includes"), ("not-includes", "not-includes")],
        ["file"] = [("not-starts-with", "not-starts-with"), ("not-ends-with", "not-ends-with")],
        ["address"] = [("wildcard", "wildcard"), ("not-wildcard", "not-wildcard")],
        ["user"] = [], ["date"] = [], ["none"] = [],
    };

    private static readonly HashSet<string> Unary = ["is_blank", "is_not_blank", "is_true", "is_false"];

    /// <summary>A value as a token reads it out of the trigger's payload (its JSON text).</summary>
    private static string AsToken(object? v) => v switch
    {
        null => string.Empty,
        PipelineFilterSqlParityTests.RangeValue r => r.AsJson() ?? string.Empty,
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };

    [Fact]
    public async Task EveryTypeOperatorAndValue_ConditionStepAnswersAsTheTriggerFilter()
    {
        var fields = PipelineFilterSqlParityTests.BuildFields();
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid() };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(fields);
        var steps = new List<PipelineStep> { new() { Id = 1, RefId = "trigger", Type = "trigger", Subtype = "new-event", ConfigJson = $"{{\"tableId\":\"{table.PublicId}\"}}" } };
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateConditionRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var report = new StringBuilder();
        int mismatches = 0, checkedCount = 0;

        for (var i = 0; i < PipelineFilterSqlParityTests.Cases.Length; i++)
        {
            var c = PipelineFilterSqlParityTests.Cases[i];
            var field = fields[i];
            var fid = field.Fid!.Value;
            var category = PipelineFilterEvaluator.GetTypeCategory(field);
            // A Condition step on a user field: "wildcard match" matches the person — covered by the user-field tests.
            var ops = Operators[category].Concat(Extra[c.Group]).Distinct();
            var engineForRule = new PipelineEngineHolder(fieldRepo, tableRepo).Engine;

            foreach (var (conditionOp, filterOp) in ops)
                foreach (var operand in Unary.Contains(conditionOp) ? [""] : c.Operands.Append(""))
                {
                    var condition = new List<string>();
                    var trigger = new List<string>();
                    foreach (var v in c.Stored)
                    {
                        var shown = PipelineFilterSqlParityTests.Fmt(v);
                        if (await (Task<bool>)evaluate.Invoke(engineForRule, [$"{{{{trigger.fid_{fid}}}}}", AsToken(v), conditionOp, operand, category, "{}", steps, fieldRepo, tableRepo, CancellationToken.None])!)
                            condition.Add(shown);
                        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = [new TriggerFilterRule { Field = $"fid_{fid}", Operator = filterOp, Value = operand }] };
                        var triggerValue = v is PipelineFilterSqlParityTests.RangeValue r ? r.AsJson() : v;
                        if (PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [fid] = triggerValue }, fields)) trigger.Add(shown);
                    }
                    checkedCount++;
                    if (condition.SequenceEqual(trigger)) continue;
                    mismatches++;
                    report.AppendLine($"{c.TypeCode,-14} {conditionOp,-22} '{operand}': condition=[{string.Join(", ", condition)}] trigger=[{string.Join(", ", trigger)}]");
                }
        }

        _output.WriteLine($"checked {checkedCount}, mismatches {mismatches}");
        var outFile = Environment.GetEnvironmentVariable("PB_PARITY_OUT");
        if (!string.IsNullOrWhiteSpace(outFile)) await File.WriteAllTextAsync(outFile + ".cond.txt", $"checked {checkedCount}, mismatches {mismatches}\n{report}");
        Assert.True(mismatches == 0, $"checked {checkedCount}, mismatches {mismatches}\n{report}");
    }

    /// <summary>The same for the computed types — every Formula variant, a Lookup of each kind, the Summary calculations: a Condition on
    /// {{trigger.fid_N}} of one answers as the trigger filter does.</summary>
    [Fact]
    public async Task EveryComputedTypeOperatorAndValue_ConditionStepAnswersAsTheTriggerFilter()
    {
        var cases = PipelineComputedFilterParityTests.Cases;
        var fields = cases.Select((c, i) => new AppField
        {
            Id = 7000 + i, Fid = 100 + i, Name = $"c{100 + i}", Label = c.TypeCode, TypeCode = c.TypeCode, Settings = c.Settings,
        }).ToList();
        var table = new AppTable { Id = 5, PublicId = Guid.NewGuid() };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        fieldRepo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(fields);
        var steps = new List<PipelineStep> { new() { Id = 1, RefId = "trigger", Type = "trigger", Subtype = "new-event", ConfigJson = $"{{\"tableId\":\"{table.PublicId}\"}}" } };
        var evaluate = typeof(PipelineEngine).GetMethod("EvaluateConditionRuleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var engine = new PipelineEngineHolder(fieldRepo, tableRepo).Engine;
        var report = new StringBuilder();
        int mismatches = 0, checkedCount = 0;

        for (var i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            var fid = fields[i].Fid!.Value;
            var category = PipelineFilterEvaluator.GetTypeCategory(fields[i]);
            foreach (var (conditionOp, filterOp) in Operators[category].Concat(Extra[c.Group]).Distinct())
                foreach (var operand in Unary.Contains(conditionOp) ? [""] : c.Operands.Append(""))
                {
                    var condition = new List<string>();
                    var trigger = new List<string>();
                    foreach (var v in c.Values)
                    {
                        var shown = PipelineFilterSqlParityTests.Fmt(v);
                        if (await (Task<bool>)evaluate.Invoke(engine, [$"{{{{trigger.fid_{fid}}}}}", AsToken(v), conditionOp, operand, category, "{}", steps, fieldRepo, tableRepo, CancellationToken.None])!)
                            condition.Add(shown);
                        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = [new TriggerFilterRule { Field = $"fid_{fid}", Operator = filterOp, Value = operand }] };
                        if (PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [fid] = v }, fields)) trigger.Add(shown);
                    }
                    checkedCount++;
                    if (condition.SequenceEqual(trigger)) continue;
                    mismatches++;
                    report.AppendLine($"{c.TypeCode} {c.Settings} {conditionOp} '{operand}': condition=[{string.Join(", ", condition)}] trigger=[{string.Join(", ", trigger)}]");
                }
        }

        _output.WriteLine($"checked {checkedCount}, mismatches {mismatches}");
        Assert.True(mismatches == 0, $"checked {checkedCount}, mismatches {mismatches}\n{report}");
    }

    /// <summary>An engine whose field / table repositories answer for the trigger's table, as the Condition step looks its tokens up.</summary>
    private sealed class PipelineEngineHolder(IAppFieldRepository fieldRepo, IAppTableRepository tableRepo)
    {
        public PipelineEngine Engine { get; } = new(
            Substitute.For<IPipelineRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<PowerBase.Application.Records.IRecordWriteService>(),
            tableRepo, fieldRepo, Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Microsoft.Extensions.Options.Options.Create(new PowerBase.Application.Common.Configurations.PipelineExecutionOptions()),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), Services(), Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());

        private static IServiceProvider Services()
        {
            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
            return sp;
        }
    }
}
