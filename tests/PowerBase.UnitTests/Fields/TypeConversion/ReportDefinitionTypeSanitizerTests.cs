using FluentAssertions;
using PowerBase.Application.Fields.TypeConversion;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Fields.TypeConversion;

public class ReportDefinitionTypeSanitizerTests
{
    private static AppField F(int fid, string type) => new() { Id = fid, Fid = fid, Name = $"f{fid}", Label = $"F{fid}", TypeCode = type };

    [Fact]
    public void Apply_SumOnFieldThatBecameText_IsSkippedWithItsSortAndAxis()
    {
        var def = new ReportDefinition
        {
            Aggregations = [new() { FieldId = 10, Function = "Sum" }, new() { FieldId = 11, Function = "Sum" }],
            SummarySortFields = [new() { Target = "Aggregation", AggregationFieldId = 10, AggregationFunction = "Sum" }, new() { Target = "Count" }],
            Chart = new ChartConfig { SecondaryAxisAggregationFieldIds = [10, 11] },
        };

        var changed = ReportDefinitionTypeSanitizer.Apply(def, [F(10, "Text"), F(11, "Currency")]);

        changed.Should().BeTrue();
        def.Aggregations.Select(a => a.FieldId).Should().Equal(11);
        def.SummarySortFields.Select(s => s.Target).Should().Equal("Count");
        def.Chart!.SecondaryAxisAggregationFieldIds.Should().Equal(11);
    }

    [Fact]
    public void Apply_SameReportWhenTypeRevertedToNumeric_KeepsEverything()
    {
        var def = new ReportDefinition { Aggregations = [new() { FieldId = 10, Function = "Sum" }] };

        ReportDefinitionTypeSanitizer.Apply(def, [F(10, "Currency")]).Should().BeFalse();
        def.Aggregations.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("Text", "DistinctCount", true)]
    [InlineData("Text", "Max", false)]
    [InlineData("Date", "Max", true)]
    [InlineData("Date", "Avg", false)]
    [InlineData("Duration", "Avg", true)]
    public void Apply_FunctionKeptOnlyWhenTypeAllowsIt(string type, string function, bool kept)
    {
        var def = new ReportDefinition { Aggregations = [new() { FieldId = 5, Function = function }] };
        ReportDefinitionTypeSanitizer.Apply(def, [F(5, type)]);
        def.Aggregations.Should().HaveCount(kept ? 1 : 0);
    }

    [Fact]
    public void Apply_ComputedAndUnknownFields_AreNeverTouched()
    {
        var def = new ReportDefinition
        {
            Aggregations = [new() { FieldId = 20, Function = "Sum" }, new() { FieldId = 99, Function = "Sum" }],
            RowGroupLevels = [new() { FieldId = 20, GroupByMode = "Increment100" }],
        };

        ReportDefinitionTypeSanitizer.Apply(def, [F(20, "Lookup")]).Should().BeFalse();
        def.Aggregations.Should().HaveCount(2);
        def.RowGroupLevels[0].GroupByMode.Should().Be("Increment100");
    }

    [Fact]
    public void Apply_NumericGroupingOnFieldThatBecameText_FallsBackToEqualValues()
    {
        var def = new ReportDefinition
        {
            GroupByFieldId = 7, GroupByMode = "Increment100",
            RowGroupLevels = [new() { FieldId = 7, GroupByMode = "Increment10" }, new() { FieldId = 8, GroupByMode = "Month" }],
            TableSortGroup = [new() { FieldId = 7, IsGroup = true, GroupByMode = "Increment1000" }],
            Chart = new ChartConfig { SeriesFieldId = 7, SeriesMode = "Increment1" },
        };

        ReportDefinitionTypeSanitizer.Apply(def, [F(7, "Text"), F(8, "Date")]).Should().BeTrue();

        def.GroupByMode.Should().Be("EqualValues");
        def.RowGroupLevels[0].GroupByMode.Should().Be("EqualValues");
        def.RowGroupLevels[1].GroupByMode.Should().Be("Month");
        def.TableSortGroup[0].GroupByMode.Should().Be("EqualValues");
        def.Chart!.SeriesMode.Should().Be("EqualValues");
    }

    [Fact]
    public void Apply_TextModeStillValidForText_Unchanged()
    {
        var def = new ReportDefinition { GroupByFieldId = 7, GroupByMode = "FirstWord" };
        ReportDefinitionTypeSanitizer.Apply(def, [F(7, "Text")]).Should().BeFalse();
        def.GroupByMode.Should().Be("FirstWord");
    }
}
