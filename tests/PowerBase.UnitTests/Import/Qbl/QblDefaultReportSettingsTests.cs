using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>A Quickbase table's "default report": the default columns are the fields flagged "add to all new reports"; the table's
/// <c>DefaultReportSettings</c> carries the default dynamic filters, quick search and sort. The converter hands them to the importer so the
/// "Default report" page shows the same as Quickbase instead of every field.</summary>
public class QblDefaultReportSettingsTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    private static string Table(string defaultReportSettings, string extraFields = "") => $"""
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
            Tables:
              $Table_Tasks:
                Type: QB::Table
                Properties:
        {defaultReportSettings}
                  Name: Tasks
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                    Properties:
                      Label: Record ID#
                      AddToAllNewReports: true
                  $Field_Task_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Task Name
                      AddToAllNewReports: true
                  $Field_Notes:
                    Type: QB::Field::Text
                    Id: 7
                    Properties:
                      Label: Notes
                      AddToAllNewReports: false
                  $Field_Status:
                    Type: QB::Field::Text
                    Id: 8
                    Properties:
                      Label: Status
                      AddToAllNewReports: true
                  $Field_Owner:
                    Type: QB::Field::Text
                    Id: 9
                    Properties:
                      Label: Owner
        {extraFields}
        """;

    private const string Settings = """
                  DefaultReportSettings:
                    DefaultFilters:
                      DynamicFields:
                        - !Ref
                          Field: $Field_Status
                        - !Ref
                          Field: $Field_Owner
                      AllowQuickSearch: false
                    DefaultSortOrder:
                      SortOrder: Descending
                      TargetField: !Ref
                        Field: $Field_Task_Name
        """;

    [Fact]
    public void Convert_DefaultColumns_AreTheFieldsFlaggedAddToAllNewReports_InFileOrder_IncludingSystemFields()
    {
        var settings = Convert(Table(Settings)).Document.Tables[0].DefaultReportSettings;

        settings.Should().NotBeNull();
        settings!.ColumnFieldNames.Should().Equal("Record ID#", "Task Name", "Status");
    }

    [Fact]
    public void Convert_DefaultDynamicFiltersQuickSearchAndSort_ComeFromTheTablesDefaultReportSettings()
    {
        var settings = Convert(Table(Settings)).Document.Tables[0].DefaultReportSettings!;

        settings.DynamicFilterType.Should().Be("Custom");
        settings.DynamicFilterFieldNames.Should().Equal("Status", "Owner");
        settings.AllowQuickSearch.Should().BeFalse();
        settings.SortFieldName.Should().Be("Task Name");
        settings.SortDescending.Should().BeTrue();
    }

    [Fact]
    public void Convert_EmptyDynamicFieldsList_MeansNoDynamicFilters()
    {
        var yaml = """
                  DefaultReportSettings:
                    DefaultFilters:
                      DynamicFields: []
                      AllowQuickSearch: true
        """;

        var settings = Convert(Table(yaml)).Document.Tables[0].DefaultReportSettings!;

        settings.DynamicFilterType.Should().Be("None");
        settings.DynamicFilterFieldNames.Should().BeEmpty();
        settings.AllowQuickSearch.Should().BeTrue();
    }

    [Fact]
    public void Convert_DynamicFieldsDefault_IsPowerBasesAutomaticDynamicFilters()
    {
        var yaml = """
                  DefaultReportSettings:
                    DefaultFilters:
                      DynamicFields: Default
                      AllowQuickSearch: true
        """;

        var settings = Convert(Table(yaml)).Document.Tables[0].DefaultReportSettings!;

        settings.DynamicFilterType.Should().Be("Default");
        settings.DynamicFilterFieldNames.Should().BeEmpty();
    }

    [Fact]
    public void Convert_SortOnADeletedField_LeavesNoSort()
    {
        var yaml = """
                  DefaultReportSettings:
                    DefaultSortOrder:
                      SortOrder: Ascending
                      TargetField: !BadRef "Referenced resource does not exist."
        """;

        var settings = Convert(Table(yaml)).Document.Tables[0].DefaultReportSettings!;

        settings.SortFieldName.Should().BeNull();
    }

    [Fact]
    public void Convert_DynamicFilterOnAFieldThatWasNotConverted_IsReportedAndLeftOut()
    {
        var yaml = """
                  DefaultReportSettings:
                    DefaultFilters:
                      DynamicFields:
                        - !Ref
                          Field: $Field_Status
                        - !Ref
                          Field: $Field_Gone
        """;

        var result = Convert(Table(yaml));

        result.Document.Tables[0].DefaultReportSettings!.DynamicFilterFieldNames.Should().Equal("Status");
        result.Issues.Should().Contain(i => i.Code == "QBL_DEFAULT_DYNAMIC_FILTER_UNAVAILABLE");
    }

    [Fact]
    public void Convert_NoFieldFlaggedAndNoSettings_LeavesPowerBaseDefaults()
    {
        var yaml = Table("").Replace("AddToAllNewReports: true", "AddToAllNewReports: false");

        Convert(yaml).Document.Tables[0].DefaultReportSettings.Should().BeNull();
    }
}
