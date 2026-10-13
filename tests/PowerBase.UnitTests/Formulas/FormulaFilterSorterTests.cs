using FluentAssertions;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Formulas;

/// <summary>
/// Conditions on a computed (Formula) field can't run in SQL, so FormulaFilterSorter judges them in memory.
/// These pin that it understands the same operators and value shapes SQL does for a stored field.
/// </summary>
public class FormulaFilterSorterTests
{
    private const int DateFid = 10;      // Formula_Date
    private const int DateTimeFid = 11;  // Formula_DateTime
    private const int NumberFid = 12;    // Formula_Number
    private const int TextFid = 13;      // Formula_Text
    private const int ListFid = 14;      // Formula_MultiSelect
    private const int StoredFid = 15;    // Number (stored)

    private static readonly List<AppField> Fields =
    [
        Field(DateFid, "Due", "Formula_Date"),
        Field(DateTimeFid, "Stamp", "Formula_DateTime"),
        Field(NumberFid, "Total", "Formula_Number"),
        Field(TextFid, "Label", "Formula_Text"),
        Field(ListFid, "Tags", "Formula_MultiSelect"),
        Field(StoredFid, "Qty", "Number"),
    ];

    private static AppField Field(int fid, string name, string typeCode) => new() { Id = fid, Fid = fid, Name = name, TypeCode = typeCode };

    private static (IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed) Pair(
        Dictionary<long, object?> computed, object? stored = null) =>
        (new Dictionary<string, object?> { [PhysicalNaming.ColumnName(StoredFid)] = stored }, computed);

    private static FilterGroup Where(long fieldId, string op, string? value, string? valueMode = null, long? valueFieldId = null) => new()
    {
        Logic = "and",
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fieldId, Operator = op, Value = value, ValueMode = valueMode, ValueFieldId = valueFieldId } }],
    };

    private static List<object?> Matching(long fieldId, FilterGroup tree, params Dictionary<long, object?>[] rows) =>
        FormulaFilterSorter.ApplyFormulaFilters(rows.Select(r => Pair(r)), tree, Fields)
            .Select(p => p.Computed.GetValueOrDefault(fieldId)).ToList();

    private static Dictionary<long, object?> Row(long fid, object? value) => new() { [fid] = value };

    // ── dates ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("date_eq", "2026-10-05", new[] { "2026-10-05" })]
    [InlineData("date_eq", "2026-10-05T00:00:00Z", new[] { "2026-10-05" })]
    [InlineData("date_ne", "2026-10-05", new[] { "2026-10-06", "2026-10-04" })]
    [InlineData("date_gt", "2026-10-05", new[] { "2026-10-06" })]
    [InlineData("date_gte", "2026-10-05", new[] { "2026-10-05", "2026-10-06" })]
    [InlineData("date_lt", "2026-10-05", new[] { "2026-10-04" })]
    [InlineData("date_lte", "2026-10-05", new[] { "2026-10-05", "2026-10-04" })]
    public void Date_operators_match_a_formula_date_even_though_it_is_ISO_text(string op, string value, string[] expected)
    {
        var result = Matching(DateFid, Where(DateFid, op, value),
            Row(DateFid, "2026-10-04"), Row(DateFid, "2026-10-05"), Row(DateFid, "2026-10-06"));

        result.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("eq", "2026-10-05", new[] { "2026-10-05T18:30:00" })]
    [InlineData("lte", "2026-10-05", new[] { "2026-10-05T18:30:00", "2026-10-04T08:00:00" })]
    [InlineData("gt", "2026-10-05", new[] { "2026-10-06T00:00:00" })]
    [InlineData("lt", "2026-10-05", new[] { "2026-10-04T08:00:00" })]
    public void A_plain_date_means_the_whole_day_for_a_formula_datetime(string op, string value, string[] expected)
    {
        var result = Matching(DateTimeFid, Where(DateTimeFid, op, value),
            Row(DateTimeFid, "2026-10-04T08:00:00"), Row(DateTimeFid, "2026-10-05T18:30:00"), Row(DateTimeFid, "2026-10-06T00:00:00"));

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void A_blank_formula_date_matches_no_date_condition()
    {
        Matching(DateFid, Where(DateFid, "date_eq", "2026-10-05"), Row(DateFid, null)).Should().BeEmpty();
        Matching(DateFid, Where(DateFid, "gte", "2026-10-05"), Row(DateFid, null)).Should().BeEmpty();
        Matching(DateFid, Where(DateFid, "ne", "2026-10-05"), Row(DateFid, null)).Should().BeEmpty();
        Matching(DateFid, Where(DateFid, "date_ne", "2026-10-05"), Row(DateFid, null)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ne", "x")]
    [InlineData("notContains", "x")]
    [InlineData("notStartsWith", "x")]
    [InlineData("notEndsWith", "x")]
    [InlineData("notWildcard", "x*")]
    [InlineData("notIncludes", "x")]
    [InlineData("notIn", "x")]
    public void Negative_operators_skip_a_blank_value_exactly_as_SQL_does_for_a_stored_field(string op, string value)
    {
        Matching(TextFid, Where(TextFid, op, value), Row(TextFid, null)).Should().BeEmpty();
        Matching(TextFid, Where(TextFid, op, value), Row(TextFid, "other")).Should().HaveCount(1);   // a value that isn't it does match
    }

    [Fact]
    public void EndsWith_and_notEndsWith_on_text()
    {
        var rows = new[] { Row(TextFid, "report.pdf"), Row(TextFid, "report.docx") };

        Matching(TextFid, Where(TextFid, "endsWith", ".PDF"), rows).Should().BeEquivalentTo(new[] { "report.pdf" });
        Matching(TextFid, Where(TextFid, "notEndsWith", ".pdf"), rows).Should().BeEquivalentTo(new[] { "report.docx" });
    }

    // ── multi-value fields: equality is about the SET of values ───────────────

    [Fact]
    public void Equality_on_a_multi_value_field_compares_the_set_in_any_order()
    {
        var rows = new[]
        {
            Row(ListFid, new List<string> { "Red", "Blue" }),
            Row(ListFid, new List<string> { "Blue", "Red", "Green" }),
            Row(ListFid, new List<string> { "Red" }),
            Row(ListFid, new List<string>()),
        };

        Matching(ListFid, Where(ListFid, "eq", "blue,red"), rows).Should().HaveCount(1);     // exactly {Red, Blue}
        Matching(ListFid, Where(ListFid, "eq", "Red"), rows).Should().HaveCount(1);          // exactly {Red}
        Matching(ListFid, Where(ListFid, "ne", "Red,Blue"), rows).Should().HaveCount(3);     // everything else, blank included
    }

    // ── address sub-fields ─────────────────────────────────────────────────────

    [Fact]
    public void A_condition_with_a_subField_filters_that_part_of_a_stored_JSON_address()
    {
        const string json = "{\"street1\":\"1 Main St\",\"city\":\"Pune\",\"zip\":\"411001\",\"country\":\"India\"}";
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = TextFid, Operator = "eq", Value = "pune", SubField = "city" } }],
        };

        FormulaFilterSorter.ApplyFormulaFilters([Pair(Row(TextFid, json))], tree, Fields).Should().HaveCount(1);

        // a value that only occurs in ANOTHER part of the address must not match
        var wrongPart = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = TextFid, Operator = "contains", Value = "India", SubField = "city" } }],
        };
        FormulaFilterSorter.ApplyFormulaFilters([Pair(Row(TextFid, json))], wrongPart, Fields).Should().BeEmpty();
    }

    // ── numbers and text ───────────────────────────────────────────────────────

    [Fact]
    public void Number_formula_equality_is_exact_not_a_substring()
    {
        var result = Matching(NumberFid, Where(NumberFid, "eq", "10"),
            Row(NumberFid, 10m), Row(NumberFid, 110m), Row(NumberFid, 10.00m));

        result.Should().HaveCount(2).And.OnlyContain(v => (decimal)v! == 10m);
    }

    [Fact]
    public void Text_operators_see_a_number_as_it_reads_on_screen()
    {
        // 5.50m is shown as "5.5"
        Matching(NumberFid, Where(NumberFid, "endsWith", "5.5"), Row(NumberFid, 5.50m)).Should().HaveCount(1);
        Matching(NumberFid, Where(NumberFid, "contains", "5.50"), Row(NumberFid, 5.50m)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("endsWith", "cd", new[] { "Abcd" })]
    [InlineData("wildcard", "a*c?", new[] { "Abcd" })]
    [InlineData("wildcard", "A?c*", new[] { "Abcd", "Abc", "Axcz!" })]
    [InlineData("notWildcard", "a*c?", new[] { "Abc", "Axcz!" })]
    public void Text_formula_supports_endsWith_and_wildcards(string op, string value, string[] expected)
    {
        var result = Matching(TextFid, Where(TextFid, op, value),
            Row(TextFid, "Abcd"), Row(TextFid, "Abc"), Row(TextFid, "Axcz!"));

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Includes_matches_a_whole_item_of_a_list_valued_formula()
    {
        var rows = new[] { Row(ListFid, new List<string> { "Red", "Blue" }), Row(ListFid, new List<string> { "Bred" }) };

        Matching(ListFid, Where(ListFid, "includes", "blue"), rows).Should().HaveCount(1);
        Matching(ListFid, Where(ListFid, "includes", "Red"), rows).Should().HaveCount(1);      // not "Bred"
        Matching(ListFid, Where(ListFid, "notIncludes", "Red"), rows).Should().HaveCount(1);   // only "Bred"
    }

    [Fact]
    public void Includes_with_several_picked_values_matches_a_list_holding_any_of_them()
    {
        var rows = new[] { Row(ListFid, new List<string> { "11", "12" }), Row(ListFid, new List<string> { "13" }), Row(ListFid, new List<string>()) };

        Matching(ListFid, Where(ListFid, "includes", "12,13"), rows).Should().HaveCount(2);      // first has 12, second has 13
        Matching(ListFid, Where(ListFid, "includes", "99,98"), rows).Should().BeEmpty();
        Matching(ListFid, Where(ListFid, "notIncludes", "12,13"), rows).Should().HaveCount(1);   // only the empty list holds none
    }

    [Fact]
    public void Includes_reads_stored_JSON_array_text_too()
    {
        Matching(TextFid, Where(TextFid, "includes", "Blue"), Row(TextFid, "[\"Red\",\"Blue\"]")).Should().HaveCount(1);
        Matching(TextFid, Where(TextFid, "includes", "Gre"), Row(TextFid, "Red,Green")).Should().BeEmpty();
    }

    // ── comparing a field to another field ─────────────────────────────────────

    [Fact]
    public void Comparing_a_stored_field_to_a_formula_field_is_judged_in_memory()
    {
        var tree = Where(StoredFid, "gt", null, valueMode: "field", valueFieldId: NumberFid);

        var (physical, memory) = FormulaFilterSorter.SplitFilterTree(tree, [NumberFid]);
        physical.Should().BeNull();
        memory.Should().NotBeNull();
        FormulaFilterSorter.TreeContainsFormulaField(tree, [NumberFid]).Should().BeTrue();

        var pairs = new[]
        {
            Pair(new Dictionary<long, object?> { [NumberFid] = 5m }, stored: 10m),   // 10 > 5
            Pair(new Dictionary<long, object?> { [NumberFid] = 50m }, stored: 10m),  // 10 > 50 no
            Pair(new Dictionary<long, object?> { [NumberFid] = null }, stored: 10m), // blank never matches
        };
        FormulaFilterSorter.ApplyFormulaFilters(pairs, memory!, Fields).Should().HaveCount(1);
    }

    [Fact]
    public void A_formula_field_can_be_compared_to_another_formula_field()
    {
        var tree = Where(DateFid, "lt", null, valueMode: "field", valueFieldId: DateTimeFid);

        var pairs = new[]
        {
            Pair(new Dictionary<long, object?> { [DateFid] = "2026-10-01", [DateTimeFid] = "2026-10-05T10:00:00" }),
            Pair(new Dictionary<long, object?> { [DateFid] = "2026-10-09", [DateTimeFid] = "2026-10-05T10:00:00" }),
        };
        FormulaFilterSorter.ApplyFormulaFilters(pairs, tree, Fields).Should().HaveCount(1);
    }

    // ── type resolution ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Formula", "{\"resultType\":\"Number\"}", "Formula_Number")]
    [InlineData("Formula", "{\"resultType\":\"Date\"}", "Formula_Date")]
    [InlineData("Formula", "{\"resultType\":\"Text\"}", "Formula")]
    [InlineData("Formula_Bool", null, "Formula_Bool")]
    [InlineData("Number", null, "Number")]
    public void The_generic_Formula_code_is_resolved_to_the_type_it_returns(string typeCode, string? settings, string expected)
    {
        FormulaFilterSorter.EffectiveTypeCode(new AppField { TypeCode = typeCode, Settings = settings }).Should().Be(expected);
    }
}
