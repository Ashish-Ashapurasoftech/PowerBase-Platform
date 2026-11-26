using FluentAssertions;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Reports;

/// <summary>A Reference is filtered by what its grid shows: the parent's key, or the original text an unlinked row kept.</summary>
public class ReferenceDisplayFilterTests
{
    private const int RefFid = 7, OriginalFid = 33, KeyFid = 11;

    private static AppField Reference(int? originalFid) => new()
    {
        Id = 70, Fid = RefFid, AppTableId = 5, Name = "Program Ref", TypeCode = "Reference",
        Settings = originalFid is int o
            ? $"{{\"relationshipId\":1,\"parentTableId\":9,\"originalFid\":{o}}}"
            : "{\"relationshipId\":1,\"parentTableId\":9}",
    };

    private static readonly AppField Original = new() { Id = 330, Fid = OriginalFid, AppTableId = 5, Name = "Program Ref (original)", TypeCode = "Text" };
    private static readonly AppField Key = new() { Id = 110, Fid = KeyFid, AppTableId = 9, Name = "Program ID", TypeCode = "Text" };

    private static FilterGroup Tree(string op, string? value = null) => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = RefFid, Operator = op, Value = value } }],
    };

    [Fact]
    public void ReferenceShowingRecordId_WithoutOriginal_IsLeftAlone()
    {
        ReferenceDisplayFilter.Rewrite(Tree("contains", "1"), [Reference(null)], new Dictionary<long, AppField>()).Should().BeNull();
    }

    [Fact]
    public void ReferenceShowingKey_ComparesTheKeyThroughAStandInLookup()
    {
        var result = ReferenceDisplayFilter.Rewrite(Tree("contains", "ABC"), [Reference(null)], new Dictionary<long, AppField> { [RefFid] = Key });

        result.Should().NotBeNull();
        var standIn = result!.Value.StandIns.Should().ContainSingle().Subject;
        standIn.TypeCode.Should().Be("Lookup");
        var cond = result.Value.Tree.Nodes.Single().Condition!;
        cond.FieldId.Should().Be(standIn.Fid);
        cond.Operator.Should().Be("contains");
        cond.Value.Should().Be("ABC");
    }

    [Fact]
    public void ConvertedReference_MatchesLinkedKeyOrUnlinkedOriginalText()
    {
        var result = ReferenceDisplayFilter.Rewrite(Tree("isNotEmpty"), [Reference(OriginalFid), Original],
            new Dictionary<long, AppField> { [RefFid] = Key });

        var or = result!.Value.Tree.Nodes.Single().Group!;
        or.Logic.Should().Be("or");
        var linked = or.Nodes[0].Group!;
        var unlinked = or.Nodes[1].Group!;

        linked.Nodes[0].Condition!.Should().Match<FilterCondition>(c => c.FieldId == RefFid && c.Operator == "isNotEmpty");
        linked.Nodes[1].Condition!.FieldId.Should().Be(result.Value.StandIns.Single().Fid!.Value);

        unlinked.Nodes[0].Condition!.Should().Match<FilterCondition>(c => c.FieldId == RefFid && c.Operator == "isEmpty");
        unlinked.Nodes[1].Condition!.Should().Match<FilterCondition>(c => c.FieldId == OriginalFid && c.Operator == "isNotEmpty");
    }

    [Fact]
    public void ConvertedReferenceShowingRecordId_StillFallsBackToOriginalText()
    {
        var result = ReferenceDisplayFilter.Rewrite(Tree("eq", "XYZ"), [Reference(OriginalFid), Original], new Dictionary<long, AppField>());

        result!.Value.StandIns.Should().BeEmpty();
        var or = result.Value.Tree.Nodes.Single().Group!;
        or.Nodes[0].Group!.Nodes[1].Condition!.FieldId.Should().Be(RefFid);
        or.Nodes[1].Group!.Nodes[1].Condition!.Should().Match<FilterCondition>(c => c.FieldId == OriginalFid && c.Value == "XYZ");
    }

    [Fact]
    public void FieldToFieldComparison_IsNotRewritten()
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = RefFid, Operator = "eq", ValueMode = "field", ValueFieldId = 2 } }],
        };
        var result = ReferenceDisplayFilter.Rewrite(tree, [Reference(OriginalFid), Original], new Dictionary<long, AppField> { [RefFid] = Key });

        var cond = result!.Value.Tree.Nodes.Single().Condition!;
        cond.FieldId.Should().Be(RefFid);
        cond.ValueMode.Should().Be("field");
    }

    [Fact]
    public void SavedTreeIsNeverChanged()
    {
        var tree = Tree("contains", "ABC");
        ReferenceDisplayFilter.Rewrite(tree, [Reference(OriginalFid), Original], new Dictionary<long, AppField> { [RefFid] = Key });
        tree.Nodes.Single().Condition!.FieldId.Should().Be(RefFid);
    }
}
