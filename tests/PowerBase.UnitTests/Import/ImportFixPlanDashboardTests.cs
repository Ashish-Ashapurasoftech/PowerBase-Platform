using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>C3.1 of docs/QBL_REIMPORT_FIX_PLAN.md: dashboards.</summary>
public class ImportFixPlanDashboardTests
{
    private const string Yaml = """
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
                Reports:
                  $Report_All:
                    Type: QB::Report::Table
                    Properties:
                      Name: All Sessions
            Pages:
              Resources:
                $Dashboard_Director:
                  Type: QB::Dashboard
                  Properties:
                    Name: Director
                  Tabs:
                    $DashboardTab_One:
                      Type: QB::Dashboard::Tab
                      Properties:
                        Name: Sessions
                      Widgets:
                        $DashboardWidget_1:
                          Type: QB::Dashboard::Widget::Table
                          Properties:
                            Title: All
                            ShowTitle: true
                            Report: !Ref
                              Table: $Table_Sessions
                              Report: $Report_All
                            X: 3
                            Y: 12
                            Width: 9
                            Height: 36
                        $DashboardWidget_2:
                          Type: QB::Dashboard::Widget::Button::OpenLink
                          Properties:
                            X: 0
                            Y: 0
                            Width: 2
                            Height: 4
                            Text: Add Session
                            Color: '#0167ff'
                            OpenInNewWindow: true
                            Link: https://example.com/add
                        $DashboardWidget_3:
                          Type: QB::Dashboard::Widget::Kpi
                          Properties:
                            X: 0
                            Y: 6
                            Width: 2
                            Height: 4
        """;

    [Fact]
    public void Dashboard_IsConvertedWithItsTabsAndWidgets_AndKpiIsReported()
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(Yaml));

        var dashboard = result.Document.Dashboards.Should().ContainSingle().Subject;
        dashboard.Name.Should().Be("Director");
        var tab = dashboard.Tabs.Should().ContainSingle().Subject;
        tab.Name.Should().Be("Sessions");
        tab.Widgets.Should().HaveCount(2);

        var report = tab.Widgets.Single(w => w.WidgetType == "Report");
        report.ReportRef.Should().Be("$Table_Sessions::$Report_All");
        (report.X, report.W).Should().Be((3, 9));
        report.H.Should().Be(6);
        report.Y.Should().Be(2);

        var button = tab.Widgets.Single(w => w.WidgetType == "ButtonBar");
        (button.ButtonLabel, button.Url, button.OpenInNewTab).Should().Be(("Add Session", "https://example.com/add", true));

        result.Issues.Should().Contain(i => i.Code == "QBL_UNSUPPORTED_DASHBOARD_WIDGET");
        result.Issues.Should().NotContain(i => i.Code == "QBL_UNSUPPORTED_PAGE_TYPE");
    }
}
