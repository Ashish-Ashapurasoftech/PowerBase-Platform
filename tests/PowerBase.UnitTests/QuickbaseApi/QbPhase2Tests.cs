using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbPhase2Tests
{
    private static readonly HashSet<int> Fids = new() { 3, 6, 7, 8 };

    [Fact]
    public void Parse_SimpleExact_ReturnsFidCondition()
    {
        var g = QbWhereParser.Parse("{6.EX.'hello'}", Fids);
        var c = g.Nodes.Single().Condition!;
        c.FieldId.Should().Be(6);
        c.Operator.Should().Be("eq");
        c.Value.Should().Be("hello");
    }

    [Fact]
    public void Parse_AndOr_KeepsLogic()
    {
        var g = QbWhereParser.Parse("{6.CT.'a'}AND({7.GT.'3'}OR{8.XEX.''})", Fids);
        g.Logic.Should().Be("AND");
        g.Nodes.Should().HaveCount(2);
        var inner = g.Nodes.Single(n => n.Group != null).Group!;
        inner.Logic.Should().Be("OR");
        inner.Nodes.Select(n => n.Condition!.Operator).Should().BeEquivalentTo(new[] { "gt", "isNotEmpty" });
    }

    [Fact]
    public void Parse_AllRecordsIdiom_Works()
    {
        var c = QbWhereParser.Parse("{3.GT.0}", Fids).Nodes.Single().Condition!;
        c.FieldId.Should().Be(3);
        c.Operator.Should().Be("gt");
    }

    [Theory]
    [InlineData("{99.EX.'x'}")]
    [InlineData("{6.ZZ.'x'}")]
    [InlineData("not a query")]
    public void Parse_Invalid_Throws400(string where)
    {
        var act = () => QbWhereParser.Parse(where, Fids);
        act.Should().Throw<QbApiException>().Which.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("10", 10.0, true)]
    [InlineData(null, "", true)]
    [InlineData("a", "b", false)]
    [InlineData(true, true, true)]
    public void SameValue_Compares(object? a, object? b, bool expected)
    {
        QbRecordService.SameValue(a, b is double d ? (decimal)d : b).Should().Be(expected);
    }
}
