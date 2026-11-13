using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Handlers;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.Import.Commands.ImportAppFromPbl;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.Entities;
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

    private static IAppRepository AppRepoWith(params App[] apps)
    {
        var repo = Substitute.For<IAppRepository>();
        repo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<App>>(apps));
        return repo;
    }

    [Fact]
    public async Task Applier_SourceAppNotFoundAnywhere_KeepsTheTableOrdinaryAndDefersIt()
    {
        var document = new PblDocument();
        document.ConnectedTables.Add(new PblConnectedTable { TableRef = "$T", TableName = "Clients", SourceAppName = "CARES Database", SourceTableName = "Clients" });
        var report = new ImportReport { AppName = "Dest App", AppPublicId = Guid.NewGuid() };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.ListByAppAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AppTable>>([new AppTable { Id = 1, PublicId = Guid.NewGuid(), AppId = 42, Name = "Clients" }]));
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();
        var sut = new PblConnectedTablesApplier(null!, AppRepoWith(), tableRepo, pendingRepo);

        await sut.ApplyAsync([(document, report)], CancellationToken.None);

        report.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("does not exist yet").And.Contain("CARES Database");
        await pendingRepo.Received(1).UpsertAsync(Arg.Is<PowerBase.Domain.ConnectedTables.ConnectedPendingImportConnection>(
            p => p.AppTableId == 1 && p.AppId == 42 && p.SourceAppName == "CARES Database" && p.SourceTableName == "Clients"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applier_SourceAppExistsButNotInThisImport_StillResolves()
    {
        // The dependency app was imported earlier, in a previous call — it is not one of the documents passed to
        // ApplyAsync, only visible through IAppRepository.ListAllAsync.
        var document = new PblDocument();
        document.ConnectedTables.Add(new PblConnectedTable
        {
            TableRef = "$T", TableName = "Clients", SourceAppName = "CARES Database", SourceTableName = "Clients",
            Fields = [new PblConnectedField { FieldName = "Name", SourceFid = 7 }],
        });
        var report = new ImportReport { AppName = "Dest App", AppPublicId = Guid.NewGuid() };
        var destApp = new App { Id = 1, PublicId = report.AppPublicId, Name = "Dest App" };
        var sourceApp = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" };

        var appRepo = AppRepoWith(destApp, sourceApp);
        appRepo.GetIdByPublicIdAsync(destApp.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(destApp.Id));
        appRepo.GetIdByPublicIdAsync(sourceApp.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(sourceApp.Id));

        var tableRepo = Substitute.For<IAppTableRepository>();
        var destTable = new AppTable { Id = 10, PublicId = Guid.NewGuid(), AppId = destApp.Id, Name = "Clients" };
        var sourceTable = new AppTable { Id = 20, PublicId = Guid.NewGuid(), AppId = sourceApp.Id, Name = "Clients" };
        tableRepo.ListByAppAsync(destApp.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AppTable>>([destTable]));
        tableRepo.ListByAppAsync(sourceApp.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AppTable>>([sourceTable]));

        var connector = Substitute.For<IExistingTableConnector>();
        connector.ConnectAsync(Arg.Any<ConnectExistingTableCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ConnectExistingTableResult(Guid.NewGuid(), 1, [])));

        var sut = new PblConnectedTablesApplier(connector, appRepo, tableRepo, Substitute.For<IConnectedPendingImportRepository>());

        await sut.ApplyAsync([(document, report)], CancellationToken.None);

        report.Skipped.Should().BeEmpty();
        await connector.Received(1).ConnectAsync(Arg.Is<ConnectExistingTableCommand>(
            c => c.DestinationTablePublicId == destTable.PublicId && c.SourceAppPublicId == sourceApp.PublicId && c.SourceTablePublicId == sourceTable.PublicId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applier_SourceAppNameAmbiguous_NeverGuesses()
    {
        var document = new PblDocument();
        document.ConnectedTables.Add(new PblConnectedTable { TableRef = "$T", TableName = "Clients", SourceAppName = "CARES Database", SourceTableName = "Clients" });
        var report = new ImportReport { AppName = "Dest App", AppPublicId = Guid.NewGuid() };
        var destApp = new App { Id = 1, PublicId = report.AppPublicId, Name = "Dest App" };
        var appRepo = AppRepoWith(destApp, new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" }, new App { Id = 3, PublicId = Guid.NewGuid(), Name = "CARES-Database" });
        appRepo.GetIdByPublicIdAsync(destApp.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(destApp.Id));
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.ListByAppAsync(destApp.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AppTable>>([new AppTable { Id = 10, PublicId = Guid.NewGuid(), AppId = destApp.Id, Name = "Clients" }]));
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();

        var sut = new PblConnectedTablesApplier(null!, appRepo, tableRepo, pendingRepo);

        await sut.ApplyAsync([(document, report)], CancellationToken.None);

        report.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("more than one application");
        await pendingRepo.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }
}
