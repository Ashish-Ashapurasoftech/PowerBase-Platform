using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Handlers;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Services;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>Cross-application connected tables: the deferred-resolution queue that completes a connected table once its
/// source application (or source table) — not available when its own app was imported — finally exists.</summary>
public class PendingConnectedTableResolverTests
{
    private static ConnectedPendingImportConnection PendingRow(long appTableId, string sourceAppName, string sourceTableName, PblConnectedTable payload, long id = 1) => new()
    {
        Id = id,
        AppId = 1,
        AppTableId = appTableId,
        SourceAppName = sourceAppName,
        SourceTableName = sourceTableName,
        PayloadJson = JsonSerializer.Serialize(payload),
        Status = ConnectedPendingImportStatuses.Pending,
    };

    [Fact]
    public async Task NoPendingRows_DoesNothing()
    {
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();
        pendingRepo.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectedPendingImportConnection>>([]));
        var appRepo = Substitute.For<IAppRepository>();
        var sut = new PendingConnectedTableResolver(pendingRepo, Substitute.For<IExistingTableConnector>(), appRepo, Substitute.For<IAppTableRepository>());

        await sut.ResolveAllAsync(CancellationToken.None);

        appRepo.DidNotReceiveWithAnyArgs().ListAllAsync(default);
    }

    [Fact]
    public async Task SourceApplicationNowExists_ConnectsAndMarksResolved()
    {
        var payload = new PblConnectedTable
        {
            TableRef = "$T", TableName = "Clients", SourceAppName = "CARES Database", SourceTableName = "Clients",
            Fields = [new PblConnectedField { FieldName = "Name", SourceFid = 7 }],
        };
        var pending = PendingRow(appTableId: 10, "CARES Database", "Clients", payload);
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();
        pendingRepo.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectedPendingImportConnection>>([pending]));

        var sourceApp = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" };
        var appRepo = Substitute.For<IAppRepository>();
        appRepo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<App>>([sourceApp]));
        appRepo.GetIdByPublicIdAsync(sourceApp.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(sourceApp.Id));

        var destinationTable = new AppTable { Id = 10, PublicId = Guid.NewGuid(), AppId = 1, Name = "Clients" };
        var sourceTable = new AppTable { Id = 20, PublicId = Guid.NewGuid(), AppId = sourceApp.Id, Name = "Clients" };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.ListByAppAsync(sourceApp.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AppTable>>([sourceTable]));
        tableRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(Task.FromResult(destinationTable));

        var connectedPublicId = Guid.NewGuid();
        var connector = Substitute.For<IExistingTableConnector>();
        connector.ConnectAsync(Arg.Any<ConnectExistingTableCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ConnectExistingTableResult(connectedPublicId, 1, [])));

        var sut = new PendingConnectedTableResolver(pendingRepo, connector, appRepo, tableRepo);

        await sut.ResolveAllAsync(CancellationToken.None);

        await connector.Received(1).ConnectAsync(Arg.Is<ConnectExistingTableCommand>(
            c => c.DestinationTablePublicId == destinationTable.PublicId && c.SourceAppPublicId == sourceApp.PublicId && c.SourceTablePublicId == sourceTable.PublicId),
            Arg.Any<CancellationToken>());
        await pendingRepo.Received(1).MarkResolvedAsync(pending.Id, connectedPublicId, Arg.Any<CancellationToken>());
        await pendingRepo.DidNotReceiveWithAnyArgs().RecordAttemptAsync(default, default!, default);
    }

    [Fact]
    public async Task SourceApplicationStillMissing_StaysPendingWithAReason()
    {
        var payload = new PblConnectedTable { TableRef = "$T", TableName = "Clients", SourceAppName = "CARES Database", SourceTableName = "Clients" };
        var pending = PendingRow(10, "CARES Database", "Clients", payload);
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();
        pendingRepo.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectedPendingImportConnection>>([pending]));
        var appRepo = Substitute.For<IAppRepository>();
        appRepo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<App>>([])); // the dependency still hasn't been imported

        var sut = new PendingConnectedTableResolver(pendingRepo, Substitute.For<IExistingTableConnector>(), appRepo, Substitute.For<IAppTableRepository>());

        await sut.ResolveAllAsync(CancellationToken.None);

        await pendingRepo.Received(1).RecordAttemptAsync(pending.Id, Arg.Is<string>(r => r.Contains("CARES Database")), Arg.Any<CancellationToken>());
        await pendingRepo.DidNotReceiveWithAnyArgs().MarkResolvedAsync(default, default, default);
    }

    [Fact]
    public async Task SourceApplicationNameNowAmbiguous_StaysPendingAndNeverGuesses()
    {
        var payload = new PblConnectedTable { TableRef = "$T", TableName = "Clients", SourceAppName = "CARES Database", SourceTableName = "Clients" };
        var pending = PendingRow(10, "CARES Database", "Clients", payload);
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();
        pendingRepo.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectedPendingImportConnection>>([pending]));
        var appRepo = Substitute.For<IAppRepository>();
        appRepo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<App>>(
            [new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" }, new App { Id = 3, PublicId = Guid.NewGuid(), Name = "CARES-Database" }]));

        var sut = new PendingConnectedTableResolver(pendingRepo, Substitute.For<IExistingTableConnector>(), appRepo, Substitute.For<IAppTableRepository>());

        await sut.ResolveAllAsync(CancellationToken.None);

        await pendingRepo.Received(1).RecordAttemptAsync(pending.Id, Arg.Is<string>(r => r.Contains("More than one application")), Arg.Any<CancellationToken>());
        await pendingRepo.DidNotReceiveWithAnyArgs().MarkResolvedAsync(default, default, default);
    }

    [Fact]
    public async Task SourceApplicationExistsButNotItsTable_StaysPendingWithAReason()
    {
        var payload = new PblConnectedTable { TableRef = "$T", TableName = "Clients", SourceAppName = "CARES Database", SourceTableName = "Clients" };
        var pending = PendingRow(10, "CARES Database", "Clients", payload);
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();
        pendingRepo.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectedPendingImportConnection>>([pending]));

        var sourceApp = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" };
        var appRepo = Substitute.For<IAppRepository>();
        appRepo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<App>>([sourceApp]));
        appRepo.GetIdByPublicIdAsync(sourceApp.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(sourceApp.Id));
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.ListByAppAsync(sourceApp.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AppTable>>([])); // table not created in that app yet

        var sut = new PendingConnectedTableResolver(pendingRepo, Substitute.For<IExistingTableConnector>(), appRepo, tableRepo);

        await sut.ResolveAllAsync(CancellationToken.None);

        await pendingRepo.Received(1).RecordAttemptAsync(pending.Id, Arg.Is<string>(r => r.Contains("Clients") && r.Contains("CARES Database")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneRowFailing_DoesNotStopTheOthersFromResolving()
    {
        var payloadA = new PblConnectedTable { TableRef = "$A", TableName = "A", SourceAppName = "Missing App", SourceTableName = "X" };
        var payloadB = new PblConnectedTable { TableRef = "$B", TableName = "B", SourceAppName = "CARES Database", SourceTableName = "Clients" };
        var rowA = PendingRow(10, "Missing App", "X", payloadA, id: 1);
        var rowB = PendingRow(11, "CARES Database", "Clients", payloadB, id: 2);
        var pendingRepo = Substitute.For<IConnectedPendingImportRepository>();
        pendingRepo.ListPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectedPendingImportConnection>>([rowA, rowB]));

        var sourceApp = new App { Id = 2, PublicId = Guid.NewGuid(), Name = "CARES Database" };
        var appRepo = Substitute.For<IAppRepository>();
        appRepo.ListAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<App>>([sourceApp]));
        appRepo.GetIdByPublicIdAsync(sourceApp.PublicId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(sourceApp.Id));

        var destinationTable = new AppTable { Id = 11, PublicId = Guid.NewGuid(), AppId = 1, Name = "B" };
        var sourceTable = new AppTable { Id = 20, PublicId = Guid.NewGuid(), AppId = sourceApp.Id, Name = "Clients" };
        var tableRepo = Substitute.For<IAppTableRepository>();
        tableRepo.ListByAppAsync(sourceApp.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AppTable>>([sourceTable]));
        tableRepo.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(Task.FromResult(destinationTable));

        var connector = Substitute.For<IExistingTableConnector>();
        connector.ConnectAsync(Arg.Any<ConnectExistingTableCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ConnectExistingTableResult(Guid.NewGuid(), 1, [])));

        var sut = new PendingConnectedTableResolver(pendingRepo, connector, appRepo, tableRepo);

        await sut.ResolveAllAsync(CancellationToken.None);

        await pendingRepo.Received(1).RecordAttemptAsync(rowA.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await pendingRepo.Received(1).MarkResolvedAsync(rowB.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
