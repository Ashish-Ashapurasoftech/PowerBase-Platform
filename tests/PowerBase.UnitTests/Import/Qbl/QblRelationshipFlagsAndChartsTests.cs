using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Settings the converter used to drop without a word: "display as link to the parent", "searchable / allowed in reports / unique" on
/// reference, lookup and summary fields, bar charts, report-formula columns, and settings PowerBase has no home for (reported, never silent).</summary>
public class QblRelationshipFlagsAndChartsTests
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
              $Table_Projects:
                Type: QB::Table
                Properties:
                  Name: Projects
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                  $Field_Project_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Project Name
                  $Field_Task_Count:
                    Type: QB::Field::Summary
                    Id: 21
                    Properties:
                      Label: Task Count
                      IsSearchable: false
                      AllowedInReports: false
                      Summary:
                        ReferenceField: !Ref
                          Table: $Table_Tasks
                          Field: $Field_Project
                        Function: Count
                  $Field_Task_Records:
                    Type: QB::Field::ReportLink
                    Id: 22
                    Properties:
                      Label: Task records
                      IsSearchable: false
                      AllowedInReports: false
                      FieldRelationship:
                        Source: !Ref
                          Field: $Field_Record_ID
                        Target: !Ref
                          Table: $Table_Tasks
                          Field: $Field_Project
                Relationships:
                  $Relationship_to_Projects_1:
                    Type: QB::Relationship::Parent
                Reports:
                  $Report_Bar:
                    Type: QB::Chart::Bar
                    Properties:
                      Name: Projects by name
                      DataDetails:
                        CategoryAxis:
                          Source: !Ref
                            Field: $Field_Project_Name
                          GroupBy: EqualValues
                        DataAxis:
                          Data:
                          - Source: !Ref
                              Field: $Field_Task_Count
                            SummarizeBy: Total
                        DataLabels: Values
                        Sorting:
                          SortBy: Category
                          Direction: Ascending
                  $Report_LineBar:
                    Type: QB::Chart::LineBar
                    Properties:
                      Name: Combo
                  $Report_Formula:
                    Type: QB::Report::Table
                    Properties:
                      Name: With formula column
                      Columns:
                      - !Ref
                        Field: $Field_Project_Name
                      - !Ref
                        ReportFormula: $ReportFormula_Weeks
              $Table_Tasks:
                Type: QB::Table
                Properties:
                  Name: Tasks
                Fields:
                  $Field_Title:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Title
                  $Field_Project:
                    Type: QB::Field::Reference
                    Id: 8
                    Properties:
                      Label: Project
                      IsSearchable: false
                      IsUnique: true
                      Reference:
                        DisplayAsLinkToParent: true
                        Relationship: !Ref
                          Relationship: $Relationship_to_Projects
                  $Field_Project_Name_Lookup:
                    Type: QB::Field::Lookup
                    Id: 9
                    Properties:
                      Label: Project Name Lookup
                      IsSearchable: false
                      AllowedInReports: false
                      Lookup:
                        DisplayAsLinkToParent: true
                        Relationship: !Ref
                          Relationship: $Relationship_to_Projects
                        TargetField: !Ref
                          Field: $Field_Project_Name
                Relationships:
                  $Relationship_to_Projects:
                    Type: QB::Relationship::Child
                    Properties:
                      Parent: !Ref
                        Table: $Table_Projects
                        Relationship: $Relationship_to_Projects_1
        """;

    [Fact]
    public void Convert_ReferenceAndLookup_CarryDisplayAsLinkToParent()
    {
        var rel = Convert(App).Document.Relationships.Single();

        rel.ReferenceDisplayAsLinkToParent.Should().BeTrue();
        rel.Lookups.Single().DisplayAsLinkToParent.Should().BeTrue();
    }

    [Fact]
    public void Convert_RelationshipFields_CarryNonDefaultSearchableReportableUnique()
    {
        var rel = Convert(App).Document.Relationships.Single();

        rel.ReferenceFlags.Should().NotBeNull();
        rel.ReferenceFlags!.IsSearchable.Should().BeFalse();
        rel.ReferenceFlags.IsUnique.Should().BeTrue();
        rel.ReferenceFlags.IsReportable.Should().BeNull();

        var lookup = rel.Lookups.Single().Flags;
        lookup.Should().NotBeNull();
        lookup!.IsSearchable.Should().BeFalse();
        lookup.IsReportable.Should().BeFalse();

        var summary = rel.Summaries.Single().Flags;
        summary.Should().NotBeNull();
        summary!.IsSearchable.Should().BeFalse();
        summary.IsReportable.Should().BeFalse();
    }

    [Fact]
    public void Convert_ReportLink_CarriesNonDefaultSearchableAndReportable()
    {
        var rel = Convert(App).Document.Relationships.Single();

        rel.ReportLink.Should().NotBeNull();
        rel.ReportLink!.Flags.Should().NotBeNull();
        rel.ReportLink.Flags!.IsSearchable.Should().BeFalse();
        rel.ReportLink.Flags.IsReportable.Should().BeFalse();
    }

    [Fact]
    public void Convert_BarChart_BecomesBarChartReport()
    {
        var result = Convert(App);
        var chart = result.Document.Tables.Single(t => t.Name == "Projects").Reports.Single(r => r.Name == "Projects by name");

        chart.ReportType.Should().Be("Chart");
        chart.Chart!.ChartType.Should().Be("Bar");
        chart.Chart.Category!.FieldName.Should().Be("Project Name");
        chart.Chart.DataLabelsVisible.Should().BeTrue();
    }

    [Fact]
    public void Convert_LineBarChart_IsReportedNotSilentlyDropped()
    {
        var result = Convert(App);

        result.Issues.Should().Contain(i => i.Code == "QBL_UNSUPPORTED_REPORT_TYPE" && i.Message.Contains("Combo"));
    }

    [Fact]
    public void Convert_ReportFormulaColumn_IsReportedAndOtherColumnsKept()
    {
        var result = Convert(App);
        var report = result.Document.Tables.Single(t => t.Name == "Projects").Reports.Single(r => r.Name == "With formula column");

        report.Columns.Should().Equal("Project Name");
        result.Issues.Should().Contain(i => i.Code == "QBL_REPORT_FORMULA_COLUMN_NOT_CARRIED" && i.Message.Contains("ReportFormula_Weeks"));
    }

    [Fact]
    public void Convert_ReportFormulaColumn_IssueIsInformationalNotAnError()
    {
        var issue = Convert(App).Issues.First(i => i.Code == "QBL_REPORT_FORMULA_COLUMN_NOT_CARRIED");

        issue.Severity.ToString().Should().Be("Warning");
    }

    [Fact]
    public void Validate_ReportLinkSharingALookupLabel_IsNotADuplicateFieldName()
    {
        var document = Convert(App).Document;
        var tasks = document.Tables.Single(t => t.Name == "Tasks");
        var projects = document.Tables.Single(t => t.Name == "Projects");
        tasks.ReportLinks.Add(new PblStandaloneReportLink
        {
            LogicalRef = "$Field_Same_Label_Link", Name = "Same label link", Label = "Project Name Lookup",
            TargetTableRef = projects.LogicalRef, TargetFieldName = "Project Name",
        });

        var result = new PblValidator().Validate(document);

        result.Issues.Should().NotContain(i => i.Code == "DUPLICATE_FIELD_NAME");
        result.IsValid.Should().BeTrue();
    }
}
