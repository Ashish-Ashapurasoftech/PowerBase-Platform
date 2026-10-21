using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Found by PipelineFilterSqlParityTests: a rule with a blank value (a rule saved without one, or a token that came out blank) dropped
/// out of Search Records — returning the whole table to the steps after it — while the trigger filters compared it with ""; and the
/// Advanced Query went to SQL unmapped and was read differently by a trigger (escaped CT values, lists, HAS).
/// </summary>
public class PipelineBlankValueAndAdvancedQueryTests
{
    private static readonly List<AppField> Fields =
    [
        new() { Id = 1, Fid = 10, Name = "Name", Label = "Name", TypeCode = "Text" },
        new() { Id = 2, Fid = 11, Name = "Amount", Label = "Amount", TypeCode = "Number" },
        new() { Id = 3, Fid = 12, Name = "Done", Label = "Done", TypeCode = "Boolean" },
        new() { Id = 4, Fid = 13, Name = "Tags", Label = "Tags", TypeCode = "MultiSelect" },
        new() { Id = 5, Fid = 14, Name = "Span", Label = "Span", TypeCode = "NumericRange" },
    ];

    private static FilterGroup Map(string field, string op, string value)
    {
        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = [new TriggerFilterRule { Field = field, Operator = op, Value = value }] };
        return (FilterGroup)PipelineFilterSqlParityTests.Map.Invoke(PipelineFilterSqlParityTests.Engine(), [group, Fields, "{}", "path", new List<PipelineStep>(), null])!;
    }

    private static IEnumerable<FilterCondition> Conditions(FilterGroup g) =>
        g.Nodes.SelectMany(n => n.Condition != null ? new[] { n.Condition } : Conditions(n.Group!));

    [Theory]
    [InlineData("is", "isEmpty")]
    [InlineData("is-not", "isNotEmpty")]
    [InlineData("wildcard", "isEmpty")]
    [InlineData("not-wildcard", "isNotEmpty")]
    public void ABlankValue_ComparesWithBlank(string op, string sqlOp) =>
        Assert.Equal(sqlOp, Assert.Single(Conditions(Map("fid_10", op, ""))).Operator);

    [Theory]
    [InlineData("contains")]
    [InlineData("starts-with")]
    [InlineData("ends-with")]
    public void ABlankValue_ContainedInEverything_LeavesNoCondition(string op) =>
        Assert.Null((FilterGroup?)PipelineFilterSqlParityTests.Map.Invoke(PipelineFilterSqlParityTests.Engine(),
            [new TriggerFilterGroup { LogicalOp = "AND", Rules = [new TriggerFilterRule { Field = "fid_10", Operator = op, Value = "" }] }, Fields, "{}", "path", new List<PipelineStep>(), null]));

    [Theory]
    [InlineData("fid_10", "not-contains")]
    [InlineData("fid_11", "greater_than")]
    [InlineData("fid_13", "includes")]
    public void ABlankValue_NothingMatches(string field, string op)
    {
        var ops = Conditions(Map(field, op, "")).Select(c => c.Operator).ToList();
        Assert.Equal(["isEmpty", "isNotEmpty"], ops);   // blank and not blank at once
    }

    [Fact]
    public void ABlankValue_OnACheckbox_IsUnchecked()
    {
        var node = Assert.Single(Map("fid_12", "is", "").Nodes);
        Assert.True(SearchFilterNodes.IsOrBlank(node));
        Assert.Equal(("eq", "false"), (SearchFilterNodes.WithoutBlank(node).Condition!.Operator, SearchFilterNodes.WithoutBlank(node).Condition!.Value));
    }

    [Fact]
    public void ARangeIsNot_LetsABlankRangeThrough()
    {
        var node = Assert.Single(Map("fid_14", "is-not", "5").Nodes);
        Assert.Equal("or", node.Group!.Logic);
        Assert.Equal([("start", "isEmpty"), ("end", "isEmpty")],
            node.Group.Nodes[1].Group!.Nodes.Select(n => (n.Condition!.SubField!, n.Condition.Operator)));
    }

    [Fact]
    public void Includes_EscapesLikeCharacters()
    {
        Assert.Equal("a[_]b", Assert.Single(Conditions(Map("fid_13", "includes", "a_b"))).Value);
    }

    // ── Advanced Query ─────────────────────────────────────────────────

    private static FilterGroup Query(string q) => CopyRecordsDefinition.ParseQuery(q, Fields)!;

    private static bool Trigger(string q, object? value, long fid) =>
        PipelineFilterEvaluator.EvaluateFilterGroup(PipelineAdvancedQuery.ForTrigger(Query(q)), new Dictionary<long, object?> { [fid] = value }, Fields);

    [Fact]
    public void AdvancedQuery_ContainsWithLikeCharacters_MatchesInATrigger() =>
        Assert.True(Trigger("{10.CT.'a_b'}", "x a_b y", 10));

    [Theory]
    [InlineData("[\"Red\"]", "{13.EX.'Red'}", true)]
    [InlineData("[\"Red\",\"Blue\"]", "{13.EX.'Blue,Red'}", true)]
    [InlineData("[\"Red\",\"Blue\"]", "{13.XEX.'Red'}", true)]
    [InlineData("[\"Red\",\"Blue\"]", "{13.HAS.'Blue'}", true)]
    [InlineData("[\"Red\"]", "{13.XHAS.'Red'}", false)]
    public void AdvancedQuery_ListsCompareTheirItems_InATrigger(string stored, string query, bool matches) =>
        Assert.Equal(matches, Trigger(query, stored, 13));

    [Theory]
    [InlineData(5.5, "{11.CT.'5'}", true)]
    [InlineData(12.0, "{11.SW.'1'}", true)]
    [InlineData(null, "{11.LT.'5'}", false)]
    public void AdvancedQuery_ANumberAsShown_InATrigger(double? stored, string query, bool matches) =>
        Assert.Equal(matches, Trigger(query, stored is null ? null : (decimal)stored.Value, 11));

    [Fact]
    public void AdvancedQuery_ForSearch_IsNot_LetsABlankThrough()
    {
        var tree = PipelineAdvancedQuery.ForSearch(Query("{10.XEX.'a'}"), Fields);
        Assert.Contains(Conditions(tree), c => c.Operator == "isEmpty");
    }

    [Fact]
    public void AdvancedQuery_ForSearch_KeepsAFieldToFieldComparison()
    {
        var tree = PipelineAdvancedQuery.ForSearch(Query("{11.GT.'_FID_11'}"), Fields);
        var c = Assert.Single(Conditions(tree));
        Assert.Equal(("field", (long?)11), (c.ValueMode, c.ValueFieldId));
    }
}
