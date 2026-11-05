using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbPhase1Tests
{
    [Fact]
    public void Format_Timestamp_IsIsoUtcWithZ()
    {
        QbValueFormatter.Format(new DateTime(2019, 12, 18, 8, 0, 0, DateTimeKind.Utc), "timestamp").Should().Be("2019-12-18T08:00:00Z");
    }

    [Fact]
    public void Format_Date_IsYyyyMmDd()
    {
        QbValueFormatter.Format(new DateTime(2019, 12, 18), "date").Should().Be("2019-12-18");
    }

    [Theory]
    [InlineData(10, 10L)]
    [InlineData("10", 10L)]
    public void Format_Numeric_WholeNumbersAreIntegers(object input, long expected)
    {
        QbValueFormatter.Format(input, "numeric").Should().Be(expected);
    }

    [Fact]
    public void Format_NumericDecimal_KeepsFraction()
    {
        QbValueFormatter.Format(12.5m, "currency").Should().Be(12.5m);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData("false", false)]
    [InlineData(1, true)]
    public void Format_Checkbox_IsBool(object input, bool expected)
    {
        QbValueFormatter.Format(input, "checkbox").Should().Be(expected);
    }

    [Fact]
    public void Format_Multitext_JsonArrayBecomesList()
    {
        QbValueFormatter.Format("[\"a\",\"b\"]", "multitext").Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void Format_User_NameBecomesUserObjectWithNullUnknowns()
    {
        var value = QbValueFormatter.Format("Jamie Stein", "user") as IDictionary<string, object?>;
        value.Should().NotBeNull();
        value!.Keys.Should().BeEquivalentTo(new[] { "email", "id", "name", "userName" });
        value["name"].Should().Be("Jamie Stein");
        value["email"].Should().BeNull();
    }

    [Fact]
    public void Format_Null_IsQuickbaseEmptyValue()
    {
        // Measured on a live realm: text-like "", numbers null, multi values [], checkbox false, file {url:"",versions:[]}.
        QbValueFormatter.Format(null, "text").Should().Be("");
        QbValueFormatter.Format(null, "date").Should().Be("");
        QbValueFormatter.Format(null, "numeric").Should().BeNull();
        QbValueFormatter.Format(null, "user").Should().BeNull();
        ((IEnumerable<object>)QbValueFormatter.Format(null, "multiuser")!).Should().BeEmpty();
        ((IEnumerable<object>)QbValueFormatter.Format(null, "multitext")!).Should().BeEmpty();
        QbValueFormatter.Format(null, "checkbox").Should().Be(false);
        ((IDictionary<string, object?>)QbValueFormatter.Format(null, "file")!)["url"].Should().Be("");
    }

    [Fact]
    public void Serialize_AndGroupWithOrSubgroup_WritesQuickbaseQuery()
    {
        var tree = new FilterGroup
        {
            Logic = "AND",
            Nodes =
            {
                new FilterNode { Condition = new FilterCondition { FieldId = 6, Operator = "contains", Value = "abc" } },
                new FilterNode
                {
                    Group = new FilterGroup
                    {
                        Logic = "OR",
                        Nodes =
                        {
                            new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "gt", Value = "10" } },
                            new FilterNode { Condition = new FilterCondition { FieldId = 8, Operator = "isEmpty" } },
                        },
                    },
                },
            },
        };
        QbQuerySerializer.Serialize(tree).Should().Be("{6.CT.'abc'}AND({7.GT.'10'}OR{8.EX.''})");
    }

    [Fact]
    public void Serialize_DateField_UsesAfterOperator()
    {
        var tree = new FilterGroup { Nodes = { new FilterNode { Condition = new FilterCondition { FieldId = 9, Operator = "gte", Value = "2024-01-01" } } } };
        QbQuerySerializer.Serialize(tree, fid => fid == 9).Should().Be("{9.OAF.'01-01-2024'}");
    }

    [Fact]
    public void Serialize_InList_BecomesOrOfExact()
    {
        var tree = new FilterGroup { Nodes = { new FilterNode { Condition = new FilterCondition { FieldId = 6, Operator = "in", Value = "[\"a\",\"b\"]" } } } };
        QbQuerySerializer.Serialize(tree).Should().Be("({6.EX.'a'}OR{6.EX.'b'})");
    }

    [Fact]
    public void Serialize_Empty_IsNull()
    {
        QbQuerySerializer.Serialize(new FilterGroup()).Should().BeNull();
        QbQuerySerializer.Serialize(null).Should().BeNull();
    }

    [Fact]
    public void BuildProperties_AlwaysHasAll73KeysAndMapsSettings()
    {
        var field = new AppField
        {
            TypeCode = "Number", Fid = 7, Description = "note", DefaultValue = "5",
            Settings = "{\"Decimals\":2,\"DisplayAs\":\"Currency\",\"Symbol\":\"$\",\"Position\":\"before\",\"ShowTotalInReports\":true,\"ColumnWidth\":120}",
        };
        var props = QbFieldPropertiesBuilder.BuildProperties(field);
        props.Should().HaveCount(73);
        props.Keys.Should().Equal(QbFieldPropertiesBuilder.PROPERTY_KEYS);
        props["decimalPlaces"].Should().Be(2);
        props["currencySymbol"].Should().Be("$");
        props["currencyFormat"].Should().Be("left");
        props["doesTotal"].Should().Be(true);
        props["width"].Should().Be(120);
        props["comments"].Should().Be("note");
        props["defaultValue"].Should().Be("5");
        props["formula"].Should().BeNull();
        QbFieldPropertiesBuilder.TypeOf(field).FieldType.Should().Be("currency");
    }

    [Fact]
    public void BuildProperties_FormulaAndLookupAndSummary()
    {
        QbFieldPropertiesBuilder.BuildProperties(new AppField { TypeCode = "Formula_Text", Settings = "{\"expression\":\"[a] & [b]\"}" })["formula"]
            .Should().Be("[a] & [b]");
        var lookup = QbFieldPropertiesBuilder.BuildProperties(new AppField { TypeCode = "Lookup", Settings = "{\"SourceFid\":9,\"ReferenceFid\":10}" });
        lookup["lookupTargetFieldId"].Should().Be(9);
        lookup["lookupReferenceFieldId"].Should().Be(10);
        var summary = QbFieldPropertiesBuilder.BuildProperties(new AppField { TypeCode = "Summary", Settings = "{\"Function\":\"Sum\",\"TargetFid\":11,\"ReferenceFid\":12}" });
        summary["summaryFunction"].Should().Be("SUM");
        summary["summaryTargetFieldId"].Should().Be(11);
    }

    [Fact]
    public void BuildProperties_BadSettingsJson_DoesNotThrow()
    {
        QbFieldPropertiesBuilder.BuildProperties(new AppField { TypeCode = "Text", Settings = "{not json" }).Should().HaveCount(73);
    }

    [Theory]
    [InlineData("Table", null, "table")]
    [InlineData("Summary", null, "summary")]
    [InlineData("Chart", "HorizontalBar", "chart")]
    [InlineData("Chart", "Pie", "chart")]
    public void QbReportType_MapsOurTypes(string reportType, string? chartType, string expected)
    {
        var def = new ReportDefinition { Chart = chartType is null ? null : new ChartConfig { ChartType = chartType } };
        QbReportService.QbReportType(reportType, def).Should().Be(expected);
    }

    [Theory]
    [InlineData("EqualValues", "equal-values")]
    [InlineData("FirstLetter", "first-letter")]
    public void Kebab_GroupingModes(string input, string expected)
    {
        QbReportService.Kebab(input).Should().Be(expected);
    }
}
