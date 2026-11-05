using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Application.Reports;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Quickbase query escapes: \' for a quote and \\ for a backslash inside a quoted value.</summary>
public class QbWhereEscapeTests
{
    private static readonly ISet<int> Fids = new HashSet<int> { 6, 7 };

    [Theory]
    [InlineData(@"{6.EX.'O\'Brien'}", "O'Brien")]
    [InlineData(@"{6.EX.'a\\b'}", @"a\b")]
    [InlineData(@"{6.EX.'\\\'x\\\''}", @"\'x\'")]
    [InlineData("{6.EX.'it''s'}", "it's")]
    [InlineData(@"{6.EX.'{6.EX.\'x\'}OR{3.GT.0}'}", "{6.EX.'x'}OR{3.GT.0}")]
    public void EscapedValues_Parse(string where, string expected)
    {
        var c = QbWhereParser.Parse(where, Fids).Nodes.Single().Condition!;
        c.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("O'Brien")]
    [InlineData(@"C:\path\'x'")]
    [InlineData("'; DROP TABLE x; --")]
    public void Serializer_And_Parser_RoundTrip(string value)
    {
        var tree = new FilterGroup { Logic = "AND", Nodes = { new FilterNode { Condition = new FilterCondition { FieldId = 6, Operator = "contains", Value = value } } } };
        var text = QbQuerySerializer.Serialize(tree)!;
        QbWhereParser.Parse(text, Fids).Nodes.Single().Condition!.Value.Should().Be(value);
    }
}
