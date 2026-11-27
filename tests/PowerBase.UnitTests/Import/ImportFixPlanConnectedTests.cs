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
                  ExternalSetIsInSolution: true
                  ExternalSetId: bteqnet5d
                  PrimaryKey:
                    - !Ref
                      ConnectedColumn: $ConnectedColumn_Client_ID
                Columns:
                  $ConnectedColumn_Client_ID:
                    Type: QB::ConnectedColumn::Text
                    Id: 6
                    Properties:
                      Name: Client ID
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
        connected.SourceQbTableId.Should().Be("bteqnet5d");
        connected.UpdateMode.Should().Be("Destructive");
        connected.CronTab.Should().Be("19 11,12,13 * * *");
        connected.RefreshKeySourceFid.Should().Be(6, "the record set's PrimaryKey (Client ID) is Quickbase's refresh key");
        connected.Fields.Should().ContainSingle().Which.Should().Match<PowerBase.Application.Import.Pbl.PblConnectedField>(f => f.FieldName == "Name" && f.SourceFid == 7);
        connected.FilterConditions.Should().ContainSingle().Which.Should().Match<PowerBase.Application.Import.Pbl.PblConnectedFilter>(f => f.SourceFid == 9 && f.Operator == "notContains" && f.Value == "Closed");
    }

    [Fact]
    public void BuildCommand_CarriesTheRefreshKey()
    {
        var command = PblConnectedTablesApplier.BuildCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new PblConnectedTable { RefreshKeySourceFid = 6, Fields = [new PblConnectedField { FieldName = "Client ID", SourceFid = 6 }] });

        command.RefreshKeySourceFid.Should().Be(6);
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

    // ---- Matching the source by its Quickbase table id (client feedback 25 Nov: data source names differ from app names) ----

    private static (PblDocument Document, ImportReport Report, App DestApp, AppTable DestTable, IAppRepository AppRepo, IAppTableRepository TableRepo) IdScenario(
        string sourceAppName, string? qbTableId, params App[] otherApps)
    {
        var document = new PblDocument();
        document.ConnectedTables.Add(new PblConnectedTable
        {
            TableRef = "$T", TableName = "Clients", SourceAppName = sourceAppName, SourceTableName = "Clients", SourceQbTableId = qbTableId,
            Fields = [new PblConnectedField { FieldName = "Name", SourceFid = 7 }],
        });
        var report = new ImportReport { AppName = "Dest App", AppPublicId = Guid.NewGuid() };
        var destApp = new App { Id = 1, PublicId = report.AppPublicId, Name = "Dest App" };
        var appRepo = AppRepoWith([destApp, .. otherApps]);
        appRepo.GetIdByPublicIdAsync(destApp.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(destApp.Id));
        var destTable = new AppTable { Id = 10, PublicId = Guid.NewGuid(), AppId = destApp.Id, Name = "Clients" };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.ListByAppAsync(destApp.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AppTable>>([destTable]));
        return (document, report, destApp, destTable, appRepo, tableRepo);
    }

    private static IExistingTableConnector Connector()
    {
        var connector = Substitute.For<IExistingTableConnector>();
        connector.ConnectAsync(Arg.Any<ConnectExistingTableCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ConnectExistingTableResult(Guid.NewGuid(), 1, [])));
        return connector;
    }

    [Fact]
    public async Task Applier_DataSourceNameDiffersFromAppName_ConnectsByQuickbaseTableId()
    {
        // The real case: the data source is "CARES Database" but the app was imported as "CARES Databases".
        var sourceApp = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Databases" };
        var s = IdScenario("CARES Database", "bteqnet5d", sourceApp);
        var sourceTablePublicId = Guid.NewGuid();
        s.TableRepo.ListByQbTableIdAsync("bteqnet5d", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<QbSourceTableMatch>>(
            [new QbSourceTableMatch(20, sourceTablePublicId, "Clients", sourceApp.Id, sourceApp.PublicId, sourceApp.Name)]));
        var connector = Connector();
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();

        await new PblConnectedTablesApplier(connector, s.AppRepo, s.TableRepo, pendingRepo).ApplyAsync([(s.Document, s.Report)], CancellationToken.None);

        s.Report.Skipped.Should().BeEmpty();
        await connector.Received(1).ConnectAsync(Arg.Is<ConnectExistingTableCommand>(
            c => c.DestinationTablePublicId == s.DestTable.PublicId && c.SourceAppPublicId == sourceApp.PublicId && c.SourceTablePublicId == sourceTablePublicId),
            Arg.Any<CancellationToken>());
        await pendingRepo.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [Fact]
    public async Task Applier_SameQuickbaseAppImportedTwice_UsesTheCopyFromThisImport()
    {
        var oldCopy = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" };
        var newCopy = new App { Id = 3, PublicId = Guid.NewGuid(), Name = "CARES Database1" };
        var s = IdScenario("CARES Database", "bteqnet5d", oldCopy, newCopy);
        var newTable = Guid.NewGuid();
        s.TableRepo.ListByQbTableIdAsync("bteqnet5d", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<QbSourceTableMatch>>(
        [
            new QbSourceTableMatch(20, Guid.NewGuid(), "Clients", oldCopy.Id, oldCopy.PublicId, oldCopy.Name),
            new QbSourceTableMatch(30, newTable, "Clients", newCopy.Id, newCopy.PublicId, newCopy.Name),
        ]));
        var newCopyReport = new ImportReport { AppName = newCopy.Name, AppPublicId = newCopy.PublicId };
        s.AppRepo.GetIdByPublicIdAsync(newCopy.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(newCopy.Id));
        var connector = Connector();

        await new PblConnectedTablesApplier(connector, s.AppRepo, s.TableRepo, Substitute.For<IConnectedPendingImportRepository>())
            .ApplyAsync([(s.Document, s.Report), (new PblDocument(), newCopyReport)], CancellationToken.None);

        await connector.Received(1).ConnectAsync(Arg.Is<ConnectExistingTableCommand>(c => c.SourceAppPublicId == newCopy.PublicId && c.SourceTablePublicId == newTable),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applier_SeveralCopiesOutsideThisImport_NarrowedByAppName()
    {
        var copyA = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" };
        var copyB = new App { Id = 3, PublicId = Guid.NewGuid(), Name = "CARES Database Test" };
        var s = IdScenario("CARES Database", "bteqnet5d", copyA, copyB);
        var tableA = Guid.NewGuid();
        s.TableRepo.ListByQbTableIdAsync("bteqnet5d", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<QbSourceTableMatch>>(
        [
            new QbSourceTableMatch(20, tableA, "Clients", copyA.Id, copyA.PublicId, copyA.Name),
            new QbSourceTableMatch(30, Guid.NewGuid(), "Clients", copyB.Id, copyB.PublicId, copyB.Name),
        ]));
        var connector = Connector();

        await new PblConnectedTablesApplier(connector, s.AppRepo, s.TableRepo, Substitute.For<IConnectedPendingImportRepository>()).ApplyAsync([(s.Document, s.Report)], CancellationToken.None);

        await connector.Received(1).ConnectAsync(Arg.Is<ConnectExistingTableCommand>(c => c.SourceTablePublicId == tableA), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applier_SeveralCopiesNoWayToChoose_NeverGuesses()
    {
        var copyA = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "Copy A" };
        var copyB = new App { Id = 3, PublicId = Guid.NewGuid(), Name = "Copy B" };
        var s = IdScenario("CARES Database", "bteqnet5d", copyA, copyB);
        s.TableRepo.ListByQbTableIdAsync("bteqnet5d", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<QbSourceTableMatch>>(
        [
            new QbSourceTableMatch(20, Guid.NewGuid(), "Clients", copyA.Id, copyA.PublicId, copyA.Name),
            new QbSourceTableMatch(30, Guid.NewGuid(), "Clients", copyB.Id, copyB.PublicId, copyB.Name),
        ]));
        var connector = Connector();

        await new PblConnectedTablesApplier(connector, s.AppRepo, s.TableRepo, Substitute.For<IConnectedPendingImportRepository>()).ApplyAsync([(s.Document, s.Report)], CancellationToken.None);

        s.Report.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("more than one application").And.Contain("Copy A").And.Contain("Copy B");
        await connector.DidNotReceiveWithAnyArgs().ConnectAsync(default!, default);
    }

    [Fact]
    public async Task Applier_NoTableCarriesTheId_FallsBackToNames()
    {
        // QBTableID not filled (no token access, or migration 098 missing): the name match still works.
        var sourceApp = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" };
        var s = IdScenario("CARES Database", "bteqnet5d", sourceApp);
        s.TableRepo.ListByQbTableIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<QbSourceTableMatch>>>(_ => throw new InvalidOperationException("Invalid column name 'QBTableID'."));
        s.AppRepo.GetIdByPublicIdAsync(sourceApp.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(sourceApp.Id));
        var sourceTable = new AppTable { Id = 20, PublicId = Guid.NewGuid(), AppId = sourceApp.Id, Name = "Clients" };
        s.TableRepo.ListByAppAsync(sourceApp.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AppTable>>([sourceTable]));
        var connector = Connector();

        await new PblConnectedTablesApplier(connector, s.AppRepo, s.TableRepo, Substitute.For<IConnectedPendingImportRepository>()).ApplyAsync([(s.Document, s.Report)], CancellationToken.None);

        await connector.Received(1).ConnectAsync(Arg.Is<ConnectExistingTableCommand>(c => c.SourceTablePublicId == sourceTable.PublicId), Arg.Any<CancellationToken>());
    }
}
