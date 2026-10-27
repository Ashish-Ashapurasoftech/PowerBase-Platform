using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>WP5 (duplicate names) and WP8 (summary reports) of the import completion work.</summary>
public class ImportCompletionReportTests
{
    private const string SummaryApp = """
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
                  $Field_User:
                    Type: QB::Field::Text
                    Properties:
                      Label: User
                  $Field_Hours:
                    Type: QB::Field::Number
                    Properties:
                      Label: Hours
                  $Field_Date:
                    Type: QB::Field::Date
                    Properties:
                      Label: Date
                Reports:
                  $Report_By_User:
                    Type: QB::Report::Summary
                    Properties:
                      Name: By User
                      GroupingAndCrosstabs:
                        Rows:
                          - Source: !Ref
                              Field: $Field_User
                            GroupBy: Value
                          - Source: !Ref
                              Field: $Field_Date
                            GroupBy: Month
                        Crosstabs:
                          Source: !Ref
                            Field: $Field_User
                          GroupBy: Value
                    Columns:
                      SummaryColumn_1:
                        Type: QB::Report::Summary::Column
                        Properties:
                          Source: CountOfRecords
                          DisplayAs: NormalValue
                      SummaryColumn_2:
                        Type: QB::Report::Summary::Column
                        Properties:
                          Source: !Ref
                            Field: $Field_Hours
                          SummarizeBy: Total
                          DisplayAs: NormalValue
                      SummaryColumn_3:
                        Type: QB::Report::Summary::Column
                        Properties:
                          Source: !Ref
                            Field: $Field_Hours
                          SummarizeBy: DistinctCount
                          DisplayAs: NormalValue
                  $Report_Empty:
                    Type: QB::Report::Summary
                    Properties:
                      Name: No Rows
                      GroupingAndCrosstabs:
                        Crosstabs: None
                    Columns:
                      SummaryColumn_1:
                        Type: QB::Report::Summary::Column
                        Properties:
                          Source: CountOfRecords
                          DisplayAs: NormalValue
                  $Report_Dup_A:
                    Type: QB::Report::Table
                    Properties:
                      Name: Embedded for X
                  $Report_Dup_B:
                    Type: QB::Report::Table
                    Properties:
                      Name: Embedded for X
        """;

    [Fact]
    public void SummaryReport_RowsCrosstabAndAggregations_AreCarried()
    {
        var report = QblToPblConverter.Convert(QblSerializer.Deserialize(SummaryApp)).Document.Tables[0].Reports.Single(r => r.Name == "By User");

        report.ReportType.Should().Be("Summary");
        report.Columns.Should().BeEmpty();
        report.SummaryRows.Select(r => (r.FieldName, r.GroupMode)).Should().Equal(("User", "EqualValues"), ("Date", "Month"));
        report.SummaryCrosstab!.FieldName.Should().Be("User");
        report.Aggregations.Should().ContainSingle().Which.Should().Match<PowerBase.Application.Import.Pbl.PblSummaryAggregation>(a => a.FieldName == "Hours" && a.Function == "Sum");
    }

    [Fact]
    public void SummaryReport_UnsupportedSummarizeFunction_IsReportedAndSkipped()
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(SummaryApp));

        result.Issues.Should().Contain(i => i.Code == "QBL_UNSUPPORTED_SUMMARY_REPORT_FUNCTION");
    }

    [Fact]
    public void SummaryReport_WithoutRows_StillConvertsSoTheImporterCanGroupByRecordId()
    {
        var report = QblToPblConverter.Convert(QblSerializer.Deserialize(SummaryApp)).Document.Tables[0].Reports.Single(r => r.Name == "No Rows");

        report.SummaryRows.Should().BeEmpty();
        report.SummaryCrosstab.Should().BeNull();
    }

    [Fact]
    public void Reports_WithTheSameName_AreKeptAndNumbered()
    {
        var reports = QblToPblConverter.Convert(QblSerializer.Deserialize(SummaryApp)).Document.Tables[0].Reports;

        reports.Select(r => r.Name).Should().Contain(["Embedded for X", "Embedded for X (2)"]);
    }

    private const string DuplicateFields = """
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
                  $Field_Date_Billed:
                    Type: QB::Field::Date
                    Properties:
                      Label: Date Billed
                  $Field_Date_Billed_1:
                    Type: QB::Field::Date
                    Properties:
                      Label: Date Billed
        """;

    [Fact]
    public void Fields_WithTheSameName_AreBothImportedTheLaterNumbered()
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(DuplicateFields));

        var fields = result.Document.Tables[0].Fields;
        fields.Should().HaveCount(2);
        fields.Select(f => f.Name).Distinct().Should().HaveCount(2);
        fields.Select(f => f.Label).Distinct().Should().HaveCount(2);
        result.Issues.Should().Contain(i => i.Code == "QBL_DUPLICATE_FIELD_NAME");
        new PowerBase.Application.Import.Pbl.PblValidator().Validate(result.Document).IsValid.Should().BeTrue();
    }
}
