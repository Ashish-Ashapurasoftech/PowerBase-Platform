using System.Reflection;
using System.Text;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// A Formula, Lookup or Summary field has no column: a trigger filter reads its value from the event (projected when the record was
/// written), and Search Records — whose SQL cannot see it — evaluates the rule in memory (PipelineComputedFilter). For every computed
/// type, every operator the filter editor offers for it and a spread of values, both must pick the same records.
/// </summary>
public class PipelineComputedFilterParityTests
{
    private readonly ITestOutputHelper _output;
    public PipelineComputedFilterParityTests(ITestOutputHelper output) => _output = output;

    internal sealed record ComputedCase(string TypeCode, string? Settings, string Group, object?[] Values, string[] Operands);

    private static readonly string[] DateOperands = ["2026-10-15", "2026-10-14T18:30:00.000Z", "2026-10-15T00:00:00.000Z"];
    private static readonly object?[] Dates = [null, new DateTime(2026, 10, 14), new DateTime(2026, 10, 15), new DateTime(2026, 10, 16)];
    private static readonly object?[] Instants = [null, new DateTime(2026, 10, 14, 20, 0, 0), new DateTime(2026, 10, 15, 10, 30, 0), new DateTime(2026, 10, 16, 5, 0, 0)];
    private static readonly object?[] Texts = [null, "", "Apple", "apple pie", "Banana"];
    private static readonly string[] TextOperands = ["apple", "pie", "A*"];
    private static readonly object?[] Numbers = [null, 0m, 5m, 5.5m, 12m];
    private static readonly string[] NumberOperands = ["5", "5.5", "0"];

    internal static readonly ComputedCase[] Cases =
    [
        new("Formula_Text", null, "text", Texts, TextOperands),
        new("Formula_RichText", null, "text", [null, "", "<p>Apple</p>", "<p>apple <b>pie</b></p>", "<p><br></p>"], ["apple", "pie", "Apple pie"]),
        new("Lookup", "{\"sourceTypeCode\":\"RichText\"}", "text", [null, "<p>Apple</p>", "<p>apple <b>pie</b></p>"], ["apple", "pie"]),
        new("Lookup", "{\"sourceTypeCode\":\"User\"}", "user", [null, "40021", "40022"], ["40021"]),
        new("Lookup", "{\"sourceTypeCode\":\"MultiUser\"}", "userList", [null, "[\"40021\"]", "[\"40021\",\"40022\"]"], ["40021", "40022,40021"]),
        new("Lookup", "{\"sourceTypeCode\":\"SingleSelect\"}", "text", [null, "Open", "Closed"], ["Open", "Clo"]),
        new("Summary", "{\"function\":\"Avg\"}", "none", [null, 0m, 2.5m, 10m], ["2.5", "0"]),
        new("Summary", "{\"function\":\"DistinctCount\"}", "none", [null, 0L, 3L], ["3", "0"]),
        new("Summary", "{\"function\":\"Min\",\"targetTypeCode\":\"Number\"}", "none", [null, 1m, 5m], ["5", "1"]),
        new("Formula_Email", null, "text", [null, "", "a@x.com", "b@y.com"], ["a@x.com", "x.com"]),
        new("Formula_Phone", null, "text", [null, "", "98765"], ["98765", "987"]),
        new("Formula_Url", null, "text", [null, "", "https://a.com"], ["https", "https://a.com"]),
        new("Formula_Number", null, "none", Numbers, NumberOperands),
        new("Formula_Duration", null, "none", Numbers, NumberOperands),
        new("Formula_Date", null, "date", Dates, DateOperands),
        new("Formula_DateTime", null, "date", Instants, DateOperands),
        new("Formula_Time", null, "none", [null, new TimeSpan(8, 0, 0), new TimeSpan(10, 30, 0)], ["10:30:00", "08:00"]),
        new("Formula_Bool", null, "none", [null, false, true], ["true", "false"]),
        new("Formula_User", null, "user", [null, "", "40021", "40022"], ["40021"]),
        new("Formula_ListUser", null, "userList", [null, "", "40021", "40021,40022"], ["40021", "40021,40022"]),
        new("Formula_MultiSelect", null, "multiSelect", [null, "", "Red", "Red,Blue", "Bred"], ["Red", "Red,Blue", "Blue,Red"]),
        new("Formula", "{\"resultType\":\"Number\"}", "none", Numbers, NumberOperands),
        new("Formula", "{\"resultType\":\"Date\"}", "none", Dates, DateOperands),
        new("Formula", "{\"resultType\":\"Bool\"}", "none", [null, false, true], ["true", "false"]),
        new("Formula", "{\"resultType\":\"Text\"}", "none", Texts, TextOperands),
        new("Lookup", "{\"sourceTypeCode\":\"Text\"}", "text", Texts, TextOperands),
        new("Lookup", "{\"sourceTypeCode\":\"Number\"}", "none", Numbers, NumberOperands),
        new("Lookup", "{\"sourceTypeCode\":\"Currency\"}", "none", Numbers, NumberOperands),
        new("Lookup", "{\"sourceTypeCode\":\"Date\"}", "date", Dates, DateOperands),
        new("Lookup", "{\"sourceTypeCode\":\"DateTime\"}", "date", Instants, DateOperands),
        new("Lookup", "{\"sourceTypeCode\":\"Time\"}", "none", [null, new TimeSpan(10, 30, 0)], ["10:30"]),
        new("Lookup", "{\"sourceTypeCode\":\"Boolean\"}", "none", [null, false, true], ["true", "false"]),
        new("Lookup", "{\"sourceTypeCode\":\"File\"}", "file", [null, "", "[{\"name\":\"Report.pdf\"}]"], ["report.pdf", "pdf"]),
        new("Lookup", "{\"sourceTypeCode\":\"MultiSelect\"}", "multiSelect", [null, "", "[\"Red\",\"Blue\"]", "Red"], ["Red", "Red,Blue"]),
        new("Summary", "{\"function\":\"Count\"}", "none", [null, 0L, 3L, 12L], ["3", "0"]),
        new("Summary", "{\"function\":\"Sum\"}", "none", Numbers, NumberOperands),
        new("Summary", "{\"function\":\"Exists\"}", "none", [null, false, true], ["true", "false"]),
        new("Summary", "{\"function\":\"CombinedText\"}", "text", Texts, TextOperands),
        new("Summary", "{\"function\":\"Max\",\"targetTypeCode\":\"Date\"}", "none", Dates, DateOperands),
    ];

    [Fact]
    public void EveryComputedTypeOperatorAndValue_TriggerAndSearchRecords_PickTheSameRecords()
    {
        var fields = Cases.Select((c, i) => new AppField
        {
            Id = 7000 + i, Fid = 100 + i, Name = $"c{100 + i}", Label = c.TypeCode, TypeCode = c.TypeCode, Settings = c.Settings,
        }).ToList();
        var engine = PipelineFilterSqlParityTests.Engine();
        var report = new StringBuilder();
        var mismatches = 0;
        var checkedCount = 0;

        for (var i = 0; i < Cases.Length; i++)
        {
            var c = Cases[i];
            var field = fields[i];
            var fid = field.Fid!.Value;
            var category = PipelineFilterEvaluator.GetTypeCategory(field);
            var ops = PipelineFilterSqlParityTests.BaseOperators[category]
                .Concat(PipelineFilterSqlParityTests.ExtraOperators[c.Group]).Distinct();

            foreach (var op in ops)
                foreach (var operand in PipelineFilterSqlParityTests.Unary.Contains(op) ? [""] : c.Operands.Append(""))
                {
                    var group = new TriggerFilterGroup
                    {
                        LogicalOp = "AND",
                        Rules = [new TriggerFilterRule { Field = $"fid_{fid}", Operator = op, Value = operand }]
                    };
                    var trigger = c.Values.Select((v, n) => (v, n))
                        .Where(x => PipelineFilterEvaluator.EvaluateGroup(group, new Dictionary<long, object?> { [fid] = x.v }, fields))
                        .Select(x => x.n).ToHashSet();

                    var tree = (FilterGroup?)PipelineFilterSqlParityTests.Map.Invoke(engine, [group, fields, "{}", "path", new List<PipelineStep>(), null]);
                    var search = tree is null
                        ? Enumerable.Range(0, c.Values.Length).ToHashSet()
                        : PipelineComputedFilter.Apply(
                                c.Values.Select((v, n) => ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = (long)n },
                                    (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [fid] = v })), tree, fields)
                            .Select(p => (int)(long)p.Row["Id"]!).ToHashSet();

                    checkedCount++;
                    if (trigger.SetEquals(search)) continue;
                    mismatches++;
                    string Show(HashSet<int> set) => "[" + string.Join(", ", set.Order().Select(n => PipelineFilterSqlParityTests.Fmt(c.Values[n]))) + "]";
                    report.AppendLine($"{c.TypeCode} {c.Settings} {op} '{operand}': search={Show(search)} trigger={Show(trigger)}");
                }
        }

        _output.WriteLine($"checked {checkedCount}, mismatches {mismatches}");
        _output.WriteLine(report.ToString());
        Assert.True(mismatches == 0, $"checked {checkedCount}, mismatches {mismatches}\n{report}");
    }
}
