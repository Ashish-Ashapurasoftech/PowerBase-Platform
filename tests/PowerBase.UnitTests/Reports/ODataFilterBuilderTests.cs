using FluentAssertions;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport.OData;

namespace PowerBase.UnitTests.Reports;

/// <summary>
/// When Azure AI Search serves a report's filter it REPLACES the filter tree with the ids it matched, so any
/// condition it can't express would silently vanish. <see cref="ODataFilterBuilder.CanExpress"/> is what keeps those
/// reports on SQL.
/// </summary>
public class ODataFilterBuilderTests
{
    private static FilterGroup Tree(params FilterCondition[] conditions) => new()
    {
        Logic = "and",
        Nodes = conditions.Select(c => new FilterNode { Condition = c }).ToList(),
    };

    private static FilterCondition Cond(string op, string? value = "x", string? subField = null, string? valueMode = null) =>
        new() { FieldId = 1, Operator = op, Value = value, SubField = subField, ValueMode = valueMode };

    [Fact]
    public void An_empty_tree_can_be_expressed()
    {
        ODataFilterBuilder.CanExpress(null).Should().BeTrue();
        ODataFilterBuilder.CanExpress(new FilterGroup()).Should().BeTrue();
    }

    [Theory]
    [InlineData("eq")]
    [InlineData("ne")]
    [InlineData("contains")]
    [InlineData("notContains")]
    [InlineData("startsWith")]
    [InlineData("notStartsWith")]
    [InlineData("gt")]
    [InlineData("in")]
    [InlineData("isEmpty")]
    public void Plain_operators_can_be_expressed(string op) =>
        ODataFilterBuilder.CanExpress(Tree(Cond(op))).Should().BeTrue();

    [Fact]
    public void An_address_sub_field_cannot_be_expressed_because_the_index_holds_the_whole_address()
    {
        ODataFilterBuilder.CanExpress(Tree(Cond("contains", "Pune", subField: "city"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("wildcard")]
    [InlineData("notWildcard")]
    [InlineData("includes")]
    [InlineData("notIncludes")]
    [InlineData("endsWith")]
    [InlineData("notEndsWith")]
    [InlineData("date_eq")]
    [InlineData("isCurrentUser")]
    public void Operators_with_no_AI_Search_translation_cannot_be_expressed(string op) =>
        ODataFilterBuilder.CanExpress(Tree(Cond(op))).Should().BeFalse();

    [Fact]
    public void Comparing_to_another_field_cannot_be_expressed()
    {
        ODataFilterBuilder.CanExpress(Tree(Cond("eq", null, valueMode: "field"))).Should().BeFalse();
        ODataFilterBuilder.CanExpress(Tree(Cond("eq", "x", valueMode: "literal"))).Should().BeTrue();
    }

    [Fact]
    public void One_inexpressible_condition_anywhere_in_the_tree_rules_it_out()
    {
        var nested = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new FilterNode { Condition = Cond("eq") },
                new FilterNode { Group = Tree(Cond("contains"), Cond("endsWith")) },
            ],
        };

        ODataFilterBuilder.CanExpress(nested).Should().BeFalse();
    }
}
