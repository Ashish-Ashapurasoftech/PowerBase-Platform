using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>Chart reports (Pie, StackedBar, Gauge) of the import completion work.</summary>
public class ImportCompletionChartTests
{
    private const string App = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Sessions:
                Type: QB::Table
                Properties:
                  Name: Sessions
                Fields:
                  $Field_Status:
                    Type: QB::Field::Text
                    Properties:
                      Label: Status
                  $Field_Category:
                    Type: QB::Field::Text
                    Properties:
                      Label: Category
                  $Field_Hours:
                    Type: QB::Field::Number
                    Properties:
                      Label: Hours
                Reports:
                  $Chart_Pie:
                    Type: QB::Chart::Pie
                    Properties:
                      DataDetails:
                        Series:
                          Source: !Ref
                            Field: $Field_Status
                          GroupBy: Value
                        DataSource: CountOfRecords
                        DataLabels: Value And Name
                      Sorting:
                        Order: Descending
                        SortBy: Value
                      Name: Sessions By Status
                  $Chart_PieByFormula:
                    Type: QB::Chart::Pie
                    Properties:
                      DataDetails:
                        Series:
                          Source: !Ref
                            ReportFormula: ReportFormula_1
                          GroupBy: Value
                        DataSource: CountOfRecords
                      Name: By Formula
                  $Chart_Stacked:
                    Type: QB::Chart::StackedBar
                    Properties:
                      DataDetails:
                        CategoryAxis:
                          Source: !Ref
                            Field: $Field_Status
                          GroupBy: Value
                        DataAxis:
                          Data:
                            - Source: !Ref
                                Field: $Field_Hours
                              SummarizeBy: Total
                        Series:
                          Source: !Ref
                            Field: $Field_Category
                          GroupBy: Value
                        Sorting:
                          SortBy: Category
                          Direction: Ascending
                        GoalLine:
                          Goal: 40
                          Label: Target
                        DataLabels: None
                      Name: Hours By Status
                  $Chart_GaugeCount:
                    Type: QB::Chart::Gauge
                    Properties:
                      Data:
                        Source: CountOfRecords
                      Name: Count Gauge
                  $Chart_GaugeHours:
                    Type: QB::Chart::Gauge
                    Properties:
                      Data:
                        Source: !Ref
                          Field: $Field_Hours
                        SummarizeBy: Average
                      GaugeLimits:
                        StartAt: 0
                        Goal: 80
                      Name: Hours Gauge
        """;

    private static List<PowerBase.Application.Import.Pbl.PblReport> Reports(out List<PowerBase.Application.Import.Pbl.PblIssue> issues)
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(App));
        issues = result.Issues;
        return result.Document.Tables[0].Reports;
    }

    [Fact]
    public void Pie_IsConvertedToAChartReportGroupedByItsField()
    {
        var pie = Reports(out _).Single(r => r.Name == "Sessions By Status");

        pie.ReportType.Should().Be("Chart");
        pie.Chart!.ChartType.Should().Be("Pie");
        pie.Chart.Category!.FieldName.Should().Be("Status");
        pie.Chart.DataLabelsVisible.Should().BeTrue();
        pie.Chart.SortBy.Should().Be("Values");
        pie.Chart.SortDirection.Should().Be("Desc");
        pie.Aggregations.Should().BeEmpty();
    }

    [Fact]
    public void Pie_GroupedByAReportFormula_IsReportedAndSkipped()
    {
        var reports = Reports(out var issues);

        reports.Should().NotContain(r => r.Name == "By Formula");
        issues.Should().Contain(i => i.Code == "QBL_UNSUPPORTED_CHART_GROUPING");
    }

    [Fact]
    public void StackedBar_CarriesCategorySeriesMeasureGoalAndSorting()
    {
        var bar = Reports(out _).Single(r => r.Name == "Hours By Status");

        bar.Chart!.ChartType.Should().Be("StackedBar");
        bar.Chart.Category!.FieldName.Should().Be("Status");
        bar.Chart.Series!.FieldName.Should().Be("Category");
        bar.Chart.GoalValue.Should().Be(40);
        bar.Chart.GoalLabel.Should().Be("Target");
        bar.Chart.DataLabelsVisible.Should().BeFalse();
        bar.Chart.SortBy.Should().Be("Labels");
        bar.Aggregations.Should().ContainSingle().Which.Should().Match<PowerBase.Application.Import.Pbl.PblSummaryAggregation>(a => a.FieldName == "Hours" && a.Function == "Sum");
    }

    [Fact]
    public void Gauge_OverAField_IsConvertedWithItsFunctionAndGoal()
    {
        var gauge = Reports(out _).Single(r => r.Name == "Hours Gauge");

        gauge.Chart!.ChartType.Should().Be("Gauge");
        gauge.Chart.GaugeFieldName.Should().Be("Hours");
        gauge.Chart.GaugeFunction.Should().Be("Avg");
        gauge.Chart.GoalValue.Should().Be(80);
    }

    [Fact]
    public void Gauge_ThatCountsRecords_IsReportedBecausePowerBaseMeasuresAField()
    {
        var reports = Reports(out var issues);

        reports.Should().NotContain(r => r.Name == "Count Gauge");
        issues.Should().Contain(i => i.Code == "QBL_UNSUPPORTED_GAUGE_COUNT");
    }
    private const string ChartsKeyApp = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Sessions:
                Type: QB::Table
                Properties:
                  Name: Sessions
                Fields:
                  $Field_Status:
                    Type: QB::Field::Text
                    Properties:
                      Label: Status
                Charts:
                  $Chart_Pie:
                    Type: QB::Chart::Pie
                    Properties:
                      DataDetails:
                        Series:
                          Source: !Ref
                            Field: $Field_Status
                          GroupBy: Value
                        DataSource: CountOfRecords
                      Name: Sessions By Status
        """;

    [Fact]
    public void Charts_UnderTheTablesChartsKey_AreConvertedLikeReports()
    {
        var reports = QblToPblConverter.Convert(QblSerializer.Deserialize(ChartsKeyApp)).Document.Tables[0].Reports;

        reports.Should().ContainSingle().Which.Should().Match<PowerBase.Application.Import.Pbl.PblReport>(r => r.ReportType == "Chart" && r.Name == "Sessions By Status");
    }
}
