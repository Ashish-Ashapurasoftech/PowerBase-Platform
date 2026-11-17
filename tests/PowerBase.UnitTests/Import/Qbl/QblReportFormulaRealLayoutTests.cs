using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Report formulas as a real Quickbase export lays them out: the formulas sit in the report's own "ReportFormulas" map (name in
/// "Label"), and the report uses them in its columns, its sort / group levels and its filter (a negative field id: -1 is the first formula,
/// -100 the second). All three uses are carried, none is dropped silently, and the imported document still validates.</summary>
public class QblReportFormulaRealLayoutTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    private const string App = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                  $Field_Programs:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Programs
                  $Field_Hours:
                    Type: QB::Field::Number
                    Id: 7
                    Properties:
                      Label: Hours
                  $Field_Active:
                    Type: QB::Field::Checkbox
                    Id: 8
                    Properties:
                      Label: Active
                Reports:
                  $Report_Wrong_Programs:
                    Type: QB::Report::Table
                    Id: 46
                    Properties:
                      Name: Wrong Programs
                      SortAndGroup:
                        - Field: !Ref
                            ReportFormula: ReportFormula_1
                          SortOrder: Descending
                          GroupBy: Unknown
                        - Field: !Ref
                            ReportFormula: ReportFormula_2
                          SortOrder: Descending
                      Columns:
                        - !Ref
                          Field: $Field_Programs
                        - !Ref
                          ReportFormula: ReportFormula_1
                        - !Ref
                          Field: $Field_Hours
                        - !Ref
                          ReportFormula: ReportFormula_2
                      Filters:
                        Criteria: |-
                          ({'8'.EX.'1'}AND({'-1'.CT.'CPST'}OR{'-100'.CT.'PSR'}))
                    ReportFormulas:
                      ReportFormula_1:
                        Type: QB::Report::Formula::Text
                        Properties:
                          Label: Open CPST
                          Formula: |-
                            If([Hours]>0,"Open CPST")
                      ReportFormula_2:
                        Type: QB::Report::Formula::Text
                        Properties:
                          Label: Open PSR
                          Formula: |-
                            If([Hours]>5,"Open PSR")
                  $Report_Only_In_Filter:
                    Type: QB::Report::Table
                    Id: 47
                    Properties:
                      Name: Filter Only
                      Columns:
                        - !Ref
                          Field: $Field_Programs
                      Filters:
                        Criteria: |-
                          ({'-1'.EX.'true'})
                    ReportFormulas:
                      ReportFormula_1:
                        Type: QB::Report::Formula::Checkbox
                        Properties:
                          Label: Is Busy
                          Formula: |-
                            [Hours]>5
                  $Report_Missing_Formula:
                    Type: QB::Report::Table
                    Id: 48
                    Properties:
                      Name: No Definition
                      Columns:
                        - !Ref
                          Field: $Field_Programs
                      Filters:
                        Criteria: |-
                          ({'-1'.CT.'x'})
        """;

    private static PblReport Report(QblConversionResult result, string name) =>
        result.Document.Tables.Single(t => t.Name == "Clients").Reports.Single(r => r.Name == name);

    private static IEnumerable<PblFilterCondition> Conditions(PblFilterGroup? group) =>
        group is null ? [] : group.Conditions.Concat(group.Groups.SelectMany(g => Conditions(g)));

    [Fact]
    public void Convert_FormulasInReportFormulasMap_AreCarriedWithTheirLabelAsName()
    {
        var report = Report(Convert(App), "Wrong Programs");

        report.ReportFormulas.Select(f => f.Name).Should().Equal("Open CPST", "Open PSR");
        report.ReportFormulas[0].ResultType.Should().Be("Text");
        report.ReportFormulas[0].Expression.Should().Contain("[Hours]>0");
        report.Columns.Should().Equal("Programs", "Open CPST", "Hours", "Open PSR");
    }

    [Fact]
    public void Convert_SortAndGroupOnReportFormulas_IsCarried()
    {
        var report = Report(Convert(App), "Wrong Programs");

        report.SortFields.Select(s => (s.FieldName, s.Desc)).Should().Equal(("Open CPST", true), ("Open PSR", true));
    }

    [Fact]
    public void Convert_FilterOnReportFormulas_IsCarriedNotDropped()
    {
        var result = Convert(App);
        var report = Report(result, "Wrong Programs");

        report.Filter.Should().NotBeNull();
        Conditions(report.Filter).Select(c => c.FieldName).Should().BeEquivalentTo("Active", "Open CPST", "Open PSR");
        result.Issues.Should().NotContain(i => i.Code == "QBL_REPORT_FILTER_UNTRANSLATABLE" && i.Message.Contains("Wrong Programs"));
    }

    [Fact]
    public void Convert_FormulaUsedOnlyInTheFilter_IsStillCarried()
    {
        var report = Report(Convert(App), "Filter Only");

        report.ReportFormulas.Select(f => f.Name).Should().Equal("Is Busy");
        report.ReportFormulas[0].ResultType.Should().Be("Bool");
        Conditions(report.Filter).Select(c => c.FieldName).Should().Equal("Is Busy");
    }

    [Fact]
    public void Convert_FilterOnAFormulaThatHasNoDefinition_IsReportedNotSilentlyDropped()
    {
        var result = Convert(App);
        var report = Report(result, "No Definition");

        report.Filter.Should().BeNull();
        result.Issues.Should().Contain(i => i.Message.Contains("No Definition") && (i.Code == "QBL_REPORT_FILTER_UNTRANSLATABLE" || i.Code == "QBL_REPORT_FORMULA_FILTER_NOT_CARRIED"));
    }

    [Fact]
    public void Validate_ReportUsingItsOwnFormulas_IsValid()
    {
        var result = Convert(App);

        var validation = new PblValidator().Validate(result.Document);

        validation.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Convert_NoNoticeSaysPowerBaseCannotHoldReportFormulas_WhenTheyWereCarried()
    {
        var result = Convert(App);

        result.Issues.Where(i => i.Message.Contains("Wrong Programs")).Should().NotContain(i => i.Code.StartsWith("QBL_REPORT_FORMULA"));
    }
}
