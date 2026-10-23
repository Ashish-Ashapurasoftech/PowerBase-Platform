using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>D1 of docs/QBL_REIMPORT_FIX_PLAN.md: Quickbase connected tables → the Connected Tables module, converted in place.</summary>
public class ImportFixPlanConnectedTests
{
    private const string Yaml = """
        Version: '0.12'
        Resources:
          $App_Dest:
            Type: QB::Application
            Properties:
              Name: Dest App
            Tables:
              $Table_Clients:
                Type: QB::Table
                Properties:
                  Name: Clients
                  ConnectedData:
                    DataSource: !Ref
                      DataSource: $DataSource_CARES_Database
                      RecordSet: $RecordSet_Clients
                    Connected: true
                    UpdateMode: Destructive
                    Schedule:
                      CronTab: 19 11,12,13 * * *
                      NotifyOnFailure: true
                    DataFilter:
                      AllOf:
                        - Source: !Ref
                            ConnectedColumn: $ConnectedColumn_Status
                          Operator: DoesNotContain
                          Value: Closed
                          ValueType: Text
                    FieldMap:
                      $Field_Name: !Ref
                        ConnectedColumn: $ConnectedColumn_Name
                Fields:
                  $Field_Name:
                    Type: QB::Field::Text
                    Properties:
                      Label: Name
        ExternalResources:
          $DataSource_CARES_Database:
            Type: QB::ConnectedDataSource::Quickbase
            Properties:
              IsPlaceholder: false
            RecordSets:
              $RecordSet_Clients:
                Type: QB::ConnectedRecordSet
                Properties:
                  Name: Clients
                Columns:
                  $ConnectedColumn_Name:
                    Type: QB::ConnectedColumn::Text
                    Id: 7
                    Properties:
                      Name: Name
                  $ConnectedColumn_Status:
                    Type: QB::ConnectedColumn::Text
                    Id: 9
                    Properties:
                      Name: Status
        """;

    [Fact]
    public void ConnectedData_IsCarriedWithItsSourceScheduleFilterAndFieldMap()
    {
        var result = QblToPblConverter.Convert(QblSerializer.Deserialize(Yaml));

        var connected = result.Document.ConnectedTables.Should().ContainSingle().Subject;
        connected.TableName.Should().Be("Clients");
        connected.SourceAppName.Should().Be("CARES Database");
        connected.SourceTableName.Should().Be("Clients");
        connected.UpdateMode.Should().Be("Destructive");
        connected.CronTab.Should().Be("19 11,12,13 * * *");
        connected.Fields.Should().ContainSingle().Which.Should().Match<PowerBase.Application.Import.Pbl.PblConnectedField>(f => f.FieldName == "Name" && f.SourceFid == 7);
        connected.FilterConditions.Should().ContainSingle().Which.Should().Match<PowerBase.Application.Import.Pbl.PblConnectedFilter>(f => f.SourceFid == 9 && f.Operator == "notContains" && f.Value == "Closed");
    }

    [Theory]
    [InlineData("19 11,12,13 * * *", ConnectedScheduleTypes.Hourly, 11, 13)]
    [InlineData("30 06 * * *", ConnectedScheduleTypes.Daily, 6, 0)]
    [InlineData("0 8 * * 1", ConnectedScheduleTypes.Weekly, 8, 0)]
    [InlineData("0 8 15 * *", ConnectedScheduleTypes.Monthly, 8, 0)]
    public void Cron_BecomesTheModulesSchedule(string cron, string type, int hour, int endHour)
    {
        var schedule = PblConnectedTablesApplier.ToSchedule(cron)!;

        schedule.Type.Should().Be(type);
        schedule.Hour.Should().Be(hour);
        if (type == ConnectedScheduleTypes.Hourly) schedule.EndHour.Should().Be(endHour);
    }

    [Fact]
    public void NoCron_MeansRefreshedByHand() => PblConnectedTablesApplier.ToSchedule(null).Should().BeNull();

    [Fact]
    public async Task Applier_SourceAppNotImported_KeepsTheTableOrdinaryAndSaysSo()
    {
        var document = new PblDocument();
        document.ConnectedTables.Add(new PblConnectedTable { TableRef = "$T", TableName = "Clients", SourceAppName = "CARES Database", SourceTableName = "Clients" });
        var report = new ImportReport { AppName = "Dest App", AppPublicId = Guid.NewGuid() };
        var sut = new PblConnectedTablesApplier(null!, Substitute.For<IAppRepository>(), Substitute.For<IAppTableRepository>());

        await sut.ApplyAsync([(document, report)], CancellationToken.None);

        report.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("not one of the imported apps").And.Contain("CARES Database");
    }
}
