using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Report-level formula columns (source: <c>QB::Report::Formula::*</c>) now map to PowerBase's own report-formula
/// feature (<see cref="PowerBase.Application.Reports.ReportFormulaFields"/>) instead of always being dropped. The definition's
/// real location in a Quickbase export is not yet confirmed against a sample file, so the converter tries two plausible
/// locations (see <c>QblToPblConverter.ResolveReportFormulaDefinition</c>) and only falls back to "not carried" when neither
/// holds a usable definition — this file covers both the resolved and the fallback case.</summary>
public class QblReportFormulaColumnTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    private const string AppWithFormulaAsSiblingOfReport = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Orders:
                Type: QB::Table
                Properties:
                  Name: Orders
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                  $Field_Qty:
                    Type: QB::Field::Number
                    Id: 6
                    Properties:
                      Label: Qty
                  $Field_Price:
                    Type: QB::Field::Currency
                    Id: 7
                    Properties:
                      Label: Price
                Reports:
                  $Report_WithFormula:
                    Type: QB::Report::Table
                    Properties:
                      Name: With formula column
                      Columns:
                      - !Ref
                        Field: $Field_Qty
                      - !Ref
                        ReportFormula: $ReportFormula_Total
                  $ReportFormula_Total:
                    Type: QB::Report::Formula::Numeric
                    Properties:
                      Name: Total
                      Formula: '[Qty] * [Price]'
        """;

    [Fact]
    public void Convert_ReportFormulaColumnDefinedAsSiblingResource_IsCarriedIntoReportFormulas()
    {
        var result = Convert(AppWithFormulaAsSiblingOfReport);
        var report = result.Document.Tables.Single(t => t.Name == "Orders").Reports.Single(r => r.Name == "With formula column");

        report.ReportFormulas.Should().ContainSingle();
        var formula = report.ReportFormulas[0];
        formula.Name.Should().Be("Total");
        formula.ResultType.Should().Be("Number");
        formula.DisplayAs.Should().BeNull();
        formula.Expression.Should().Be("[Qty] * [Price]");

        report.Columns.Should().Equal("Qty", "Total");
        result.Issues.Should().NotContain(i => i.Code == "QBL_REPORT_FORMULA_COLUMN_NOT_CARRIED");
    }

    [Fact]
    public void Convert_ReportFormulaColumnWithNoResolvableDefinition_FallsBackToNotCarriedWarning()
    {
        const string app = """
            Version: '0.12'
            Resources:
              $App_Test:
                Type: QB::Application
                Properties:
                  Name: Test App
                Tables:
                  $Table_Orders:
                    Type: QB::Table
                    Properties:
                      Name: Orders
                    Fields:
                      $Field_Record_ID:
                        Type: QB::Field::RecordID
                        Id: 3
                      $Field_Qty:
                        Type: QB::Field::Number
                        Id: 6
                        Properties:
                          Label: Qty
                    Reports:
                      $Report_WithFormula:
                        Type: QB::Report::Table
                        Properties:
                          Name: With formula column
                          Columns:
                          - !Ref
                            Field: $Field_Qty
                          - !Ref
                            ReportFormula: $ReportFormula_Missing
            """;

        var result = Convert(app);
        var report = result.Document.Tables.Single(t => t.Name == "Orders").Reports.Single(r => r.Name == "With formula column");

        report.ReportFormulas.Should().BeEmpty();
        report.Columns.Should().Equal("Qty");
        result.Issues.Should().Contain(i => i.Code == "QBL_REPORT_FORMULA_COLUMN_NOT_CARRIED");
    }

    [Fact]
    public void Convert_FormulaDefinitionSiblingNode_IsNeverMistakenForAReportItself()
    {
        var result = Convert(AppWithFormulaAsSiblingOfReport);
        var table = result.Document.Tables.Single(t => t.Name == "Orders");

        table.Reports.Should().ContainSingle();
        result.Issues.Should().NotContain(i => i.Code == "QBL_UNSUPPORTED_REPORT_TYPE");
    }
}
