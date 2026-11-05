using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Application.Reports;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbWhereParserBlankAndDateTests
{
    private static readonly ISet<int> Fids = new HashSet<int> { 3, 6, 7, 8, 9 };

    [Theory]
    [InlineData("XCT", "notContains")]
    [InlineData("XSW", "notStartsWith")]
    [InlineData("XEX", "ne")]
    public void NegativeTextOperators_AlsoMatchBlank(string qbOp, string ourOp)
    {
        var g = QbWhereParser.Parse($"{{6.{qbOp}.'abc'}}", Fids);
        var either = g.Nodes.Single().Group!;
        either.Logic.Should().Be("OR");
        either.Nodes.Select(n => n.Condition!.Operator).Should().BeEquivalentTo(new[] { ourOp, "isEmpty" });
    }

    [Fact]
    public void PositiveOperator_IsPlainCondition()
    {
        var g = QbWhereParser.Parse("{6.CT.'abc'}", Fids);
        g.Nodes.Single().Condition!.Operator.Should().Be("contains");
    }

    [Fact]
    public void Today_IsResolvedToLiteralDate_NotIsEmpty()
    {
        var g = QbWhereParser.Parse("{7.AF.'today'}", Fids);
        var c = Flatten(g).Single();
        c.Operator.Should().NotBe("isEmpty");
        c.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void InRange_UnknownPeriod_MatchesNothing_LikeQuickbase()
    {
        // Live Quickbase: IR understands today / yesterday / tomorrow; 'this year' matches nothing (XIR: everything).
        QbWhereParser.QuickbaseOrder("{7.IR.'this year'}").Should().Be("{3.EX.''}");
        QbWhereParser.QuickbaseOrder("{7.XIR.'this year'}").Should().Be("{3.XEX.''}");
        QbWhereParser.QuickbaseOrder("{7.IR.'today'}").Should().Be("{7.EX.'today'}");
    }

    [Fact]
    public void AndOr_AreLeftToRight_LikeQuickbase() =>
        QbWhereParser.QuickbaseOrder("{6.EX.'a'}OR{6.EX.'b'}AND{7.GT.10}").Should().Be("(({6.EX.'a'})OR({6.EX.'b'}))AND({7.GT.10})");

    [Fact]
    public void LowerCaseConnector_IsInvalid()
    {
        var act = () => QbWhereParser.Parse("{6.EX.'a'}or{6.EX.'b'}", Fids);
        act.Should().Throw<QbApiException>();
    }

    [Fact]
    public void UnquotedValue_StopsAtFirstDot() => QbWhereParser.QuickbaseOrder("{8.EX.301.5}").Should().Be("{8.EX.301}");

    [Fact]
    public void QuotedValue_KeepsDots() => QbWhereParser.QuickbaseOrder("{8.EX.'301.5'}").Should().Be("{8.EX.'301.5'}");

    [Fact]
    public void FieldComparison_KeepsValueField()
    {
        var c = Flatten(QbWhereParser.Parse("{8.GT._FID_9}", Fids)).Single();
        c.ValueMode.Should().Be("field");
        c.ValueFieldId.Should().Be(9);
    }

    [Fact]
    public void AskPrompt_IsRejected()
    {
        var act = () => QbWhereParser.Parse("{6.EX._ask1_}", Fids);
        act.Should().Throw<QbApiException>();
    }

    private static IEnumerable<FilterCondition> Flatten(FilterGroup g) =>
        g.Nodes.SelectMany(n => n.Condition is { } c ? new[] { c } : Flatten(n.Group!));
}
