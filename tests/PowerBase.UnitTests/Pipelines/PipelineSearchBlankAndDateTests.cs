using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Search Records used to answer some rules differently from the trigger filters (found by PipelineFilterSqlParityTests):
/// "is not" / "does not contain"… dropped records with a blank value (SQL's NOT on a NULL), "is false" dropped a blank checkbox, and the
/// in-memory half read a picked day ("…Z") in the server's own time zone.
/// </summary>
public class PipelineSearchBlankAndDateTests
{
    private static readonly List<AppField> Fields =
    [
        new() { Id = 1, Fid = 10, Name = "Name", Label = "Name", TypeCode = "Text" },
        new() { Id = 2, Fid = 11, Name = "Amount", Label = "Amount", TypeCode = "Number" },
        new() { Id = 3, Fid = 12, Name = "Done", Label = "Done", TypeCode = "Boolean" },
        new() { Id = 4, Fid = 13, Name = "Due", Label = "Due", TypeCode = "Date" },
        new() { Id = 5, Fid = 14, Name = "Address", Label = "Address", TypeCode = "Address" },
    ];

    private static FilterGroup Map(string field, string op, string value)
    {
        var group = new TriggerFilterGroup { LogicalOp = "AND", Rules = [new TriggerFilterRule { Field = field, Operator = op, Value = value }] };
        return (FilterGroup)PipelineFilterSqlParityTests.Map.Invoke(PipelineFilterSqlParityTests.Engine(), [group, Fields, "{}", "path", new List<PipelineStep>(), null])!;
    }

    [Theory]
    [InlineData("fid_10", "is-not", "a")]
    [InlineData("fid_10", "not-contains", "a")]
    [InlineData("fid_10", "not-starts-with", "a")]
    [InlineData("fid_10", "not-ends-with", "a")]
    [InlineData("fid_10", "not-wildcard", "a*")]
    [InlineData("fid_11", "is-not", "5")]
    [InlineData("fid_13", "is-not", "2026-10-15")]
    public void ANegatedRule_LetsABlankRecordThrough(string field, string op, string value)
    {
        var node = Assert.Single(Map(field, op, value).Nodes);
        Assert.True(SearchFilterNodes.IsOrBlank(node));
        Assert.Equal(int.Parse(field[4..]), node.Group!.Nodes[1].Condition!.FieldId);
    }

    [Theory]
    [InlineData("fid_10", "is", "a")]
    [InlineData("fid_10", "contains", "a")]
    [InlineData("fid_11", "greater_than", "5")]
    [InlineData("fid_13", "is", "2026-10-15")]
    public void APositiveRule_DoesNot(string field, string op, string value) =>
        Assert.False(SearchFilterNodes.IsOrBlank(Assert.Single(Map(field, op, value).Nodes)));

    /// <summary>Found running a PowerFlow in the app: "Text is not" with no value saved. The SQL builder skips a condition with no value,
    /// so the search returned every record (or, wrapped in "or blank", only the blank ones). It now compares with "" as a trigger does:
    /// "is not" holds for every record with a value, "does not contain" / "does not wildcard match"… as the trigger says.</summary>
    [Theory]
    [InlineData("is-not", "isNotEmpty")]
    [InlineData("not-wildcard", "isNotEmpty")]
    public void ANegatedRuleWithNoValue_ComparesWithBlank(string op, string sqlOp)
    {
        var node = Assert.Single(Map("fid_10", op, "").Nodes);
        Assert.Equal(sqlOp, node.Condition!.Operator);
    }

    [Fact]
    public void ANegatedRuleOnAnAddressPart_ChecksThatPartForBlank()
    {
        var node = Assert.Single(Map("fid_14.city", "is-not", "Surat").Nodes);
        Assert.True(SearchFilterNodes.IsOrBlank(node));
        Assert.Equal("city", node.Group!.Nodes[1].Condition!.SubField);
    }

    [Theory]
    [InlineData("is_false", "", "false", true)]
    [InlineData("is", "false", "false", true)]
    [InlineData("is-not", "true", "false", true)]
    [InlineData("is_true", "", "true", false)]
    [InlineData("is", "true", "true", false)]
    [InlineData("is-not", "false", "true", false)]
    public void ACheckbox_BlankIsUnchecked(string op, string value, string state, bool blankPasses)
    {
        var node = Assert.Single(Map("fid_12", op, value).Nodes);
        var condition = SearchFilterNodes.WithoutBlank(node).Condition!;
        Assert.Equal(("eq", state), (condition.Operator, condition.Value));
        Assert.Equal(blankPasses, SearchFilterNodes.IsOrBlank(node));

        // The in-memory half (an encrypted app) gives the same answer for a blank checkbox.
        var blank = PipelineComputedFilter.Apply(
            [((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = 1L, ["f_12"] = null }, new Dictionary<long, object?>())],
            Map("fid_12", op, value), Fields);
        Assert.Equal(blankPasses, blank.Count == 1);
    }

    [Theory]
    [InlineData("is", "2026-10-15T00:00:00.000Z", true)]
    [InlineData("is", "2026-10-14T18:30:00.000Z", true)]   // 15 Oct picked in India: the instant of its midnight
    [InlineData("is-after", "2026-10-15T00:00:00.000Z", false)]
    [InlineData("is-before", "2026-10-15T00:00:00.000Z", false)]
    [InlineData("is-on-or-after", "2026-10-15T00:00:00.000Z", true)]
    public void InMemory_APickedDay_IsReadAsUtc_WhateverTheServersTimeZone(string op, string value, bool matches)
    {
        var row = ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = 1L, ["f_13"] = new DateTime(2026, 10, 15) },
            (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>());
        var picked = PipelineComputedFilter.Apply([row], Map("fid_13", op, value), Fields);
        Assert.Equal(matches, picked.Count == 1);
    }
}
