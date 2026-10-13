using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using PowerBase.Application.Common.Filtering;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// An encrypted field reaches the in-memory filter decrypted — as text, whatever its type. Comparisons must still follow the field's
/// type: a Number "&gt; 10000" compared as text matched "9000" ("9" &gt; "1") and returned almost every record.
/// </summary>
public class EncryptedFieldInMemoryFilterTests
{
    private static readonly List<AppField> Fields = new()
    {
        new() { Id = 6, Fid = 6, Name = "Name", TypeCode = "Text", IsEncrypted = true },
        new() { Id = 7, Fid = 7, Name = "Number", TypeCode = "Number", IsEncrypted = true },
        new() { Id = 8, Fid = 8, Name = "When", TypeCode = "Date", IsEncrypted = true },
        new() { Id = 9, Fid = 9, Name = "Active", TypeCode = "Boolean", IsEncrypted = true },
        new() { Id = 10, Fid = 10, Name = "Price", TypeCode = "Currency", IsEncrypted = true },
    };

    // Decrypted rows: every value is a string, as FieldEncryptionContext returns it.
    private static readonly int[] Numbers = { 5, 999, 8000, 8001, 9000, 9999, 10000, 10001, 25000, 100000, 700011 };

    private static IReadOnlyDictionary<string, object?> Row(int id, int number) => new Dictionary<string, object?>
    {
        ["Id"] = (long)id,
        ["f_6"] = $"Name{number}",
        ["f_7"] = number.ToString(CultureInfo.InvariantCulture),
        ["f_8"] = new DateTime(2026, 1, 1).AddDays(number % 300).ToString("o"),
        ["f_9"] = number % 2 == 0 ? "true" : "false",
        ["f_10"] = (number / 100m).ToString(CultureInfo.InvariantCulture),
    };

    private static List<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)> Rows() =>
        Numbers.Select((n, i) => (Row(i + 1, n), (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>())).ToList();

    private static FilterGroup Where(long fid, string op, string value) => new()
    {
        Logic = "and", Nodes = new List<FilterNode> { new() { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value } } }
    };

    private static int[] PipelineMatches(long fid, string op, string value) =>
        PipelineComputedFilter.Apply(Rows(), Where(fid, op, value), Fields).Select(p => int.Parse((string)p.Row["f_7"]!)).ToArray();

    public static IEnumerable<object[]> NumberCases() => new[]
    {
        new object[] { "gt", "8000" }, new object[] { "gt", "10000" }, new object[] { "gte", "10000" },
        new object[] { "lt", "10000" }, new object[] { "lte", "9999" }, new object[] { "eq", "9000" },
        new object[] { "ne", "9000" }, new object[] { "gt", "999.5" }, new object[] { "lt", "6" },
        new object[] { "in", "[\"8000\",\"700011\"]" }, new object[] { "notIn", "8000,9000" },
    };

    private static int[] Expected(string op, string value)
    {
        var list = value.TrimStart().StartsWith('[')
            ? value.Trim('[', ']').Split(',').Select(v => v.Trim('"')).ToList()
            : value.Split(',').ToList();
        var single = list.Count == 1 ? decimal.Parse(list[0], CultureInfo.InvariantCulture) : 0m;
        bool Test(int n) => op switch
        {
            "gt" => n > single, "gte" => n >= single, "lt" => n < single, "lte" => n <= single,
            "eq" => n == single, "ne" => n != single,
            "in" => list.Any(x => decimal.Parse(x, CultureInfo.InvariantCulture) == n),
            "notIn" => !list.Any(x => decimal.Parse(x, CultureInfo.InvariantCulture) == n),
            _ => throw new ArgumentException(op)
        };
        return Numbers.Where(Test).ToArray();
    }

    [Theory]
    [MemberData(nameof(NumberCases))]
    public void PipelineFilter_EncryptedNumber_ComparesAsNumbers(string op, string value) =>
        PipelineMatches(7, op, value).Should().Equal(Expected(op, value));

    [Fact]
    public void TheReportedCase_GreaterThan10000_IsNotTheWholeTable()
    {
        PipelineMatches(7, "gt", "10000").Should().Equal(10001, 25000, 100000, 700011);
        PipelineMatches(7, "gt", "8000").Should().Equal(8001, 9000, 9999, 10000, 10001, 25000, 100000, 700011);
    }

    [Fact]
    public void EncryptedCurrency_ComparesAsNumbers() =>
        PipelineMatches(10, "gte", "100").Should().Equal(10000, 10001, 25000, 100000, 700011);

    [Fact]
    public void EncryptedDate_ComparesAsDates_ForInstantAndCalendarDayOperators()
    {
        var day100 = new DateTime(2026, 1, 1).AddDays(100).ToString("o");
        PipelineMatches(8, "gt", day100).Should().Equal(Numbers.Where(n => n % 300 > 100).ToArray());
        PipelineMatches(8, "date_eq", day100).Should().Equal(Numbers.Where(n => n % 300 == 100).ToArray());
    }

    [Fact]
    public void EncryptedBoolean_ComparesAsCheckbox()
    {
        PipelineMatches(9, "eq", "true").Should().Equal(Numbers.Where(n => n % 2 == 0).ToArray());
        PipelineMatches(9, "eq", "false").Should().Equal(Numbers.Where(n => n % 2 != 0).ToArray());
    }

    [Fact]
    public void EncryptedText_KeepsTextSemantics()
    {
        PipelineMatches(6, "contains", "900").Should().Equal(9000);
        PipelineMatches(6, "startsWith", "name1").Should().Equal(10000, 10001, 100000);
        PipelineMatches(6, "eq", "NAME999").Should().Equal(999);
        // Text ordering stays text ordering, as for an unencrypted Text field.
        PipelineMatches(6, "gt", "Name9").Should().Equal(999, 9000, 9999);
    }

    // ───────────── operators the in-memory filter was missing or mis-reading ─────────────

    private static string[] NameMatches(string op, string value, params string[] names)
    {
        var rows = names.Select((n, i) => ((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = (long)i, ["f_6"] = n },
            (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()));
        return PipelineComputedFilter.Apply(rows, Where(6, op, value), Fields).Select(p => (string)p.Row["f_6"]!).ToArray();
    }

    [Fact]
    public void EndsWith_MatchesOnlyTheEnding_InsteadOfEveryRecord()
    {
        NameMatches("endsWith", "kot", "Rajkot", "Surat", "KOT", "Kotdwar").Should().Equal("Rajkot", "KOT");
        NameMatches("notEndsWith", "kot", "Rajkot", "Surat", "KOT", "Kotdwar").Should().Equal("Surat", "Kotdwar");
    }

    [Theory]
    [InlineData("contains", "a[_]b", new[] { "xa_bx" })]           // advanced query CT.'a_b'
    [InlineData("startsWith", "50[%]", new[] { "50% off" })]      // advanced query SW.'50%'
    [InlineData("endsWith", "[[]1]", new[] { "item[1]" })]
    [InlineData("notContains", "a[_]b", new[] { "axb", "50% off", "item[1]" })]
    public void LikeEscapedValues_AreComparedAsPlainText(string op, string value, string[] expected) =>
        NameMatches(op, value, "xa_bx", "axb", "50% off", "item[1]").Should().Equal(expected);

    [Fact]
    public void Wildcard_EscapedStar_IsALiteralStar()
    {
        NameMatches("wildcard", "a\\*b", "a*b", "axxb").Should().Equal("a*b");
        NameMatches("wildcard", "a*b", "a*b", "axxb", "ab").Should().Equal("a*b", "axxb", "ab");
        NameMatches("wildcard", "R?jkot", "Rajkot", "Rjkot").Should().Equal("Rajkot");
    }

    [Theory]
    [InlineData("50%_off[1]")]
    [InlineData("plain")]
    [InlineData("[[]")]
    [InlineData("a[%]b")]
    public void EscapeLike_RoundTrips(string text) =>
        PipelineComputedFilter.UnescapeLike(PipelineComputedFilter.EscapeLike(text)).Should().Be(text);

    [Fact]
    public void LookupOfAnEncryptedNumber_ComparesAsANumber()
    {
        var lookup = new AppField { Id = 20, Fid = 20, Name = "Parent Number", TypeCode = "Lookup",
            Settings = "{\"relationshipId\":1,\"referenceFid\":9,\"sourceFid\":7,\"sourceTypeCode\":\"Number\"}" };
        var fields = Fields.Append(lookup).ToList();
        var rows = new[] { "9000", "10001", "700011" }.Select((v, i) => (
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = (long)i },
            (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [20] = v }));
        PipelineFilterEvaluator.GetTypeCategory(lookup).Should().Be("NUMBER");

        var matched = PipelineComputedFilter.Apply(rows, Where(20, "gt", "10000"), fields).Select(p => (string)p.Computed[20]!).ToArray();

        matched.Should().Equal("10001", "700011");
    }

    [Theory]
    [InlineData("Number", "8000", "8000.0000")]
    [InlineData("Number", " 8000.50 ", "8000.5")]
    [InlineData("Currency", "12", "12.00")]
    public void CopyRecords_EncryptedNumberMergeKey_MatchesByValue(string typeCode, string a, string b) =>
        CopyRecordsExecutor.EncryptedMergeKey(new AppField { TypeCode = typeCode }, a)
            .Should().Be(CopyRecordsExecutor.EncryptedMergeKey(new AppField { TypeCode = typeCode }, b));

    [Theory]
    [InlineData("Text", "Abc ", "Abc ")]
    [InlineData("Text", "8000.0000", "8000.0000")]
    [InlineData("Number", "not a number", "not a number")]
    public void CopyRecords_EncryptedMergeKey_LeavesTextAsItIs(string typeCode, string key, string expected) =>
        CopyRecordsExecutor.EncryptedMergeKey(new AppField { TypeCode = typeCode }, key).Should().Be(expected);

    [Theory]
    [InlineData("Number", "12.5", 12.5)]
    [InlineData("Currency", "700011.0000", 700011)]
    [InlineData("Percent", "-3", -3)]
    [InlineData("Rating", "4", 4)]
    public void Coercion_NumberLikeText_BecomesDecimal(string typeCode, string text, double expected) =>
        StoredValueCoercion.ToFieldType(text, new AppField { TypeCode = typeCode }).Should().Be((decimal)expected);

    [Theory]
    [InlineData("Text", "123")]
    [InlineData("Number", "not a number")]
    [InlineData("NumericRange", "{\"min\":1}")]
    [InlineData("Number", "")]
    public void Coercion_LeavesOtherValuesUnchanged(string typeCode, string text) =>
        StoredValueCoercion.ToFieldType(text, new AppField { TypeCode = typeCode }).Should().Be(text);

    [Fact]
    public void Coercion_TypedValuesAndNullAreUnchanged()
    {
        StoredValueCoercion.ToFieldType(5m, new AppField { TypeCode = "Number" }).Should().Be(5m);
        StoredValueCoercion.ToFieldType(null, new AppField { TypeCode = "Number" }).Should().BeNull();
        StoredValueCoercion.ToFieldType("5", null).Should().Be("5");
    }
}
