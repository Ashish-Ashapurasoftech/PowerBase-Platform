using System.Text.Json;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// The candidates AI Search proposes are read from SQL as "the original filter AND Record ID# is one of these (and, for a
/// snapshotted search, no newer than the snapshot)". The restriction must never loosen the filter it is added to.
/// </summary>
public class PipelineIdRestrictionTests
{
    private static FilterNode Cond(int fid, string op, string value) => new() { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value } };

    [Fact]
    public void NoFilter_IsJustTheRestriction()
    {
        var result = PipelineEngine.WithIdRestriction(null, [5, 6], null);

        Assert.Equal("and", result.Logic);
        var only = Assert.Single(result.Nodes);
        Assert.Equal(3, only.Condition!.FieldId);
        Assert.Equal("in", only.Condition.Operator);
        Assert.Equal(new long[] { 5, 6 }, JsonSerializer.Deserialize<long[]>(only.Condition.Value!));
    }

    [Fact]
    public void AndFilter_GetsTheRestrictionAsOneMoreAndedCondition_WithoutNesting()
    {
        var tree = new FilterGroup { Logic = "and", Nodes = [Cond(7, "eq", "a"), Cond(8, "eq", "b")] };

        var result = PipelineEngine.WithIdRestriction(tree, [1], null);

        Assert.Equal("and", result.Logic);
        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(7, result.Nodes[0].Condition!.FieldId);
        Assert.Equal(8, result.Nodes[1].Condition!.FieldId);
        Assert.Equal(3, result.Nodes[2].Condition!.FieldId);
    }

    [Fact]
    public void OrFilter_IsNestedSoTheRestrictionAppliesToAllOfIt()
    {
        var tree = new FilterGroup { Logic = "or", Nodes = [Cond(7, "eq", "a"), Cond(8, "eq", "b")] };

        var result = PipelineEngine.WithIdRestriction(tree, [1], null);

        Assert.Equal("and", result.Logic);                       // not "or": a candidate must satisfy the restriction AND the filter
        Assert.Equal(2, result.Nodes.Count);
        Assert.Equal(3, result.Nodes[0].Condition!.FieldId);
        Assert.Same(tree, result.Nodes[1].Group);
    }

    [Fact]
    public void Snapshot_AddsAnUpperBoundOnTheRecordId()
    {
        var tree = new FilterGroup { Nodes = [Cond(7, "eq", "a")] };

        var result = PipelineEngine.WithIdRestriction(tree, [1], 999);

        var bound = Assert.Single(result.Nodes, n => n.Condition is { Operator: "lte" });
        Assert.Equal(3, bound.Condition!.FieldId);
        Assert.Equal("999", bound.Condition.Value);
    }

    [Fact]
    public void WithoutASnapshot_NoUpperBoundIsAdded() =>
        Assert.DoesNotContain(PipelineEngine.WithIdRestriction(new FilterGroup { Nodes = [Cond(7, "eq", "a")] }, [1], null).Nodes,
            n => n.Condition is { Operator: "lte" });

    [Fact]
    public void TheOriginalFilterIsNotModified()
    {
        var tree = new FilterGroup { Logic = "and", Nodes = [Cond(7, "eq", "a")] };
        var before = JsonSerializer.Serialize(tree);

        PipelineEngine.WithIdRestriction(tree, [1, 2, 3], 10);

        Assert.Equal(before, JsonSerializer.Serialize(tree));
    }

    [Fact]
    public void EmptyFilterGroup_IsTreatedAsNoFilter()
    {
        var result = PipelineEngine.WithIdRestriction(new FilterGroup(), [1], null);

        Assert.Single(result.Nodes);
        Assert.Equal(3, result.Nodes[0].Condition!.FieldId);
    }

    [Fact]
    public void LargeIdLists_AreCarriedWhole()
    {
        var ids = Enumerable.Range(1, 2000).Select(i => (long)i).ToArray();

        var result = PipelineEngine.WithIdRestriction(null, ids, null);

        Assert.Equal(ids, JsonSerializer.Deserialize<long[]>(result.Nodes[0].Condition!.Value!));
    }
}
