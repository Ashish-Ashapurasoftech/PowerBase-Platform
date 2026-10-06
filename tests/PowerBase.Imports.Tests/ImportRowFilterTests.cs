using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

public class ImportRowFilterTests
{
    private static AppField F(int fid, string type) => new() { Fid = fid, Name = $"F{fid}", TypeCode = type };
    private static readonly AppField[] Fields = [F(6, "Text"), F(7, "Number"), F(8, "Date"), F(9, "MultiSelect"), F(10, "Boolean")];

    private static IReadOnlyDictionary<string, object?> Row(string? text = "Hello World", decimal number = 5m, string? multi = "[\"Red\",\"Blue\"]", bool flag = true) =>
        new Dictionary<string, object?> { ["f_6"] = text, ["f_7"] = number, ["f_8"] = new DateTime(2026, 3, 10), ["f_9"] = multi, ["f_10"] = flag };

    private static bool Match(string op, long fid, string? value, IReadOnlyDictionary<string, object?>? row = null, string? valueMode = null, long? valueFid = null) =>
        new ImportRowFilter(new FilterGroup { Nodes = [new() { Condition = new() { FieldId = fid, Operator = op, Value = value, ValueMode = valueMode, ValueFieldId = valueFid } }] }, Fields)
            .Matches(row ?? Row());

    [Theory]
    [InlineData("eq", 6, "hello world", true)]
    [InlineData("ne", 6, "hello world", false)]
    [InlineData("contains", 6, "lo wo", true)]
    [InlineData("notContains", 6, "xyz", true)]
    [InlineData("startsWith", 6, "hello", true)]
    [InlineData("endsWith", 6, "WORLD", true)]
    [InlineData("wildcard", 6, "h*d", true)]
    [InlineData("wildcard", 6, "h?llo*", true)]
    [InlineData("notWildcard", 6, "h*d", false)]
    [InlineData("gt", 7, "4", true)]
    [InlineData("lte", 7, "4.99", false)]
    [InlineData("in", 7, "[\"1\",\"5\"]", true)]
    [InlineData("notIn", 7, "1,2", true)]
    [InlineData("includes", 9, "Red", true)]
    [InlineData("includes", 9, "Re", false)]
    [InlineData("notIncludes", 9, "Green", true)]
    [InlineData("date_eq", 8, "2026-03-10", true)]
    [InlineData("date_ne", 8, "2026-03-10", false)]
    [InlineData("gte", 8, "2026-03-11", false)]
    [InlineData("eq", 10, "true", true)]
    public void Operators_match_the_sql_builders_semantics(string op, long fid, string value, bool expected) =>
        Match(op, fid, value).Should().Be(expected);

    [Fact]
    public void Null_values_satisfy_no_comparison_but_do_satisfy_is_empty()
    {
        var row = Row(text: null);
        Match("ne", 6, "x", row).Should().BeFalse();
        Match("notContains", 6, "x", row).Should().BeFalse();
        Match("isEmpty", 6, null, row).Should().BeTrue();
        Match("isNotEmpty", 6, null, row).Should().BeFalse();
    }

    [Fact]
    public void A_condition_with_no_value_is_ignored_like_in_sql() => Match("eq", 6, "", Row()).Should().BeTrue();

    [Fact]
    public void Unknown_operators_fail_closed() => Match("mystery", 6, "x").Should().BeFalse();

    [Fact]
    public void Field_to_field_comparison_reads_both_values_from_the_row() =>
        Match("gt", 7, null, Row(number: 9m), valueMode: "field", valueFid: 9).Should().BeFalse(); // 9 vs the text "[...]"

    [Fact]
    public void Nested_groups_honour_and_or_logic()
    {
        var tree = new FilterGroup
        {
            Logic = "or",
            Nodes =
            [
                new() { Condition = new() { FieldId = 7, Operator = "gt", Value = "100" } },
                new() { Group = new FilterGroup { Logic = "and", Nodes = [
                    new() { Condition = new() { FieldId = 6, Operator = "startsWith", Value = "hello" } },
                    new() { Condition = new() { FieldId = 10, Operator = "eq", Value = "true" } } ] } }
            ]
        };
        new ImportRowFilter(tree, Fields).Matches(Row()).Should().BeTrue();
        new ImportRowFilter(tree, Fields).Matches(Row(flag: false)).Should().BeFalse();
    }
}
