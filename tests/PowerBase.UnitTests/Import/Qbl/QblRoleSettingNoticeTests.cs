using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Quickbase role settings PowerBase has no home for (hidden app menu items, hidden table buttons, "refresh connected data", role order)
/// are reported per role instead of being dropped silently.</summary>
public class QblRoleSettingNoticeTests
{
    private static QblConversionResult Convert(string yaml) => QblToPblConverter.Convert(QblSerializer.Deserialize(yaml));

    private const string App = """
        Version: '0.12'
        Resources:
          $App_Test:
            Type: QB::Application
            Properties:
              Name: Test App
              RoleOrder:
              - !Ref
                Role: $Role_Viewer
              - !Ref
                Role: $Role_Clerk
            Roles:
              $Role_Viewer:
                Type: QB::Application::Role
                Properties:
                  Name: Viewer
                  AppUI:
                    HideSearch: true
                    HideHelp: true
                    HideSettings: false
                  TableUI:
                    Global:
                      HideNewRecord: true
                      HidePrint: false
                    $Table_Things:
                      HidePrint: true
              $Role_Clerk:
                Type: QB::Application::Role
                Properties:
                  Name: Clerk
                  AppUI:
                    HideSearch: false
                  TableUI:
                    Global:
                      HideNewRecord: false
            Tables:
              $Table_Things:
                Type: QB::Table
                Properties:
                  Name: Things
                  RolePermissions:
                    $Role_Clerk:
                      CanAddRecords: true
                      CanDeleteRecords: false
                      CanEditFieldProperties: false
                      CanSaveCommonReports: false
                      CanRefreshConnectedData: true
                      CanViewRecords:
                        When: Always
                      CanModifyRecords:
                        When: Always
                Fields:
                  $Field_Record_ID:
                    Type: QB::Field::RecordID
                    Id: 3
                  $Field_Name:
                    Type: QB::Field::Text
                    Id: 6
                    Properties:
                      Label: Name
        """;

    [Fact]
    public void Convert_RoleHidingMenuItemsAndButtons_ReportsEachInPlainWords()
    {
        var notices = Convert(App).Issues.Where(i => i.Code == "QBL_ROLE_SETTING_NOT_CARRIED").Select(i => i.Message).ToList();

        notices.Should().Contain(m => m.Contains("Role 'Viewer'") && m.Contains("search") && m.Contains("help") && !m.Contains("settings"));
        notices.Should().Contain(m => m.Contains("Role 'Viewer'") && m.Contains("every table") && m.Contains("new record") && !m.Contains("print"));
        notices.Should().Contain(m => m.Contains("Role 'Viewer'") && m.Contains("1 specific table"));
    }

    [Fact]
    public void Convert_RoleThatHidesNothing_ProducesNoHidingNotice()
    {
        var notices = Convert(App).Issues.Where(i => i.Code == "QBL_ROLE_SETTING_NOT_CARRIED" && i.Message.Contains("Role 'Clerk'")).ToList();

        notices.Should().NotContain(i => i.Message.Contains("hides"));
    }

    [Fact]
    public void Convert_RefreshConnectedDataPermission_IsReportedForTheRole()
    {
        var notice = Convert(App).Issues.Single(i => i.Code == "QBL_ROLE_SETTING_NOT_CARRIED" && i.Message.Contains("refresh connected data"));

        notice.Message.Should().Contain("Role 'Clerk'").And.Contain("'Things'");
    }

    [Fact]
    public void Convert_RoleOrder_IsReportedOnce()
    {
        Convert(App).Issues.Count(i => i.Code == "QBL_ROLE_ORDER_NOT_CARRIED").Should().Be(1);
    }

    [Theory]
    [InlineData("QBL_ROLE_SETTING_NOT_CARRIED")]
    [InlineData("QBL_ROLE_ORDER_NOT_CARRIED")]
    public void Classifier_RoleNotices_AreInformational(string code)
    {
        var (severity, _) = PowerBase.Application.Import.Reporting.ImportIssueClassifier.Classify(code, "Role 'X': something", PowerBase.Application.Import.Reporting.ImportIssueSeverity.Warning);

        severity.Should().Be(PowerBase.Application.Import.Reporting.ImportIssueSeverity.Information);
    }
}
