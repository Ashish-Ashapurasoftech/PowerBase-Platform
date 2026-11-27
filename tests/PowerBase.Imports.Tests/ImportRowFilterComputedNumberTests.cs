using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>
/// The import's source filter judges a Summary or Lookup field on its projected value (it has no column, so SQL can't),
/// and the import screens now offer it the numeric operators — "Client - # of Session is equal to 1" — instead of the
/// text ones. These pin that the in-memory filter then compares such a number as a number, whatever numeric type the
/// projection produced, and not as text ("10" is greater than "9").
/// </summary>
public class ImportRowFilterComputedNumberTests
{
    private const int LookupFid = 20, SummaryFid = 21;

    private static readonly AppField[] Fields =
    [
        new() { Fid = LookupFid, Name = "Client - # of Session", TypeCode = "Lookup" },
        new() { Fid = SummaryFid, Name = "# of Session", TypeCode = "Summary" },
    ];

    private static bool Match(long fid, string op, string? value, object? cell) =>
        new ImportRowFilter(
            new FilterGroup { Nodes = [new() { Condition = new() { FieldId = fid, Operator = op, Value = value } }] }, Fields)
        .Matches(new Dictionary<string, object?> { [$"f_{fid}"] = cell });

    public static IEnumerable<object[]> NumberShapes() =>
    [
        [1], [1L], [1m], [1.0], [(short)1], ["1"],
    ];

    [Theory]
    [MemberData(nameof(NumberShapes))]
    public void A_number_lookup_is_equal_to_its_value_whatever_numeric_type_the_projection_made(object cell)
    {
        Match(LookupFid, "eq", "1", cell).Should().BeTrue();
        Match(LookupFid, "eq", "2", cell).Should().BeFalse();
        Match(LookupFid, "ne", "2", cell).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(NumberShapes))]
    public void A_number_summary_is_equal_to_its_value_whatever_numeric_type_the_projection_made(object cell)
    {
        Match(SummaryFid, "eq", "1", cell).Should().BeTrue();
        Match(SummaryFid, "gte", "1", cell).Should().BeTrue();
        Match(SummaryFid, "gt", "1", cell).Should().BeFalse();
    }

    [Theory]
    [InlineData(10, "gt", "9", true)]    // as text, "10" < "9"
    [InlineData(10, "lt", "9", false)]
    [InlineData(2, "lt", "10", true)]    // as text, "2" > "10"
    [InlineData(2, "gte", "10", false)]
    public void A_number_is_compared_as_a_number_not_as_text(int cell, string op, string value, bool expected)
    {
        Match(SummaryFid, op, value, cell).Should().Be(expected);
        Match(LookupFid, op, value, cell).Should().Be(expected);
    }

    [Fact]
    public void A_decimal_summary_compares_to_a_decimal_value() =>
        Match(SummaryFid, "gt", "4.5", 4.75m).Should().BeTrue();

    [Fact]
    public void A_summary_with_no_value_satisfies_no_comparison_but_is_empty()
    {
        Match(SummaryFid, "eq", "0", null).Should().BeFalse();
        Match(SummaryFid, "ne", "0", null).Should().BeFalse();
        Match(SummaryFid, "isEmpty", null, null).Should().BeTrue();
    }

    [Fact]
    public void A_number_value_in_a_list_matches_on_the_number() =>
        Match(LookupFid, "in", "[\"1\",\"3\"]", 3m).Should().BeTrue();
}
