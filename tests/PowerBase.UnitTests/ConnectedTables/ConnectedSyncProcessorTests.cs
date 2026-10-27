using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables;
using PowerBase.Application.ConnectedTables.Access;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.ConnectedTables.Scheduling;
using PowerBase.Application.ConnectedTables.Sync;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.ConnectedTables;

/// <summary>The processor's lifecycle paths: what happens before any source row is read.</summary>
public class ConnectedSyncProcessorTests
{
    private readonly IConnectedRunRepository _runs = Substitute.For<IConnectedRunRepository>();
    private readonly IConnectedTableRepository _tables = Substitute.For<IConnectedTableRepository>();
    private readonly IConnectedConnectionRepository _connections = Substitute.For<IConnectedConnectionRepository>();
    private readonly IConnectedControlRepository _control = Substitute.For<IConnectedControlRepository>();
    private readonly IConnectedDataWriter _writer = Substitute.For<IConnectedDataWriter>();
    private readonly IConnectedTableLock _lock = Substitute.For<IConnectedTableLock>();
    private readonly IAppTableRepository _appTables = Substitute.For<IAppTableRepository>();
    private readonly IAuditRepository _audit = Substitute.For<IAuditRepository>();
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IQueryContext _context = Substitute.For<IQueryContext>();
    private readonly ConnectedSyncProcessor _processor;

    private readonly ConnectedTable _table = new()
    {
        Id = 4, PublicId = Guid.NewGuid(), AppId = 2, AppTableId = 40, ConnectionId = 9, OwnerUserId = 7, SourceTenantId = 1, SourceTableName = "Clients",
        ScheduleJson = ConnectedScheduling.Serialize(new ConnectedSchedule { Type = "daily", Hour = 3 }),
    };
    private readonly ConnectedRun _run;

    public ConnectedSyncProcessorTests()
    {
        _context.TenantId.Returns(1);
        _run = new ConnectedRun { Id = 50, PublicId = Guid.NewGuid(), ConnectedTableId = 4, Status = ConnectedRunStatuses.Queued, TriggeredBy = ConnectedRunTriggers.Schedule,
            SnapshotJson = JsonSerializer.Serialize(ConnectedRunSnapshot.Of(_table)) };
        _runs.GetByPublicIdAsync(_run.PublicId, Arg.Any<CancellationToken>()).Returns(_run);
        _tables.GetByIdAsync(4, Arg.Any<CancellationToken>()).Returns(_table);
        _lock.TryAcquireAsync(4, Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        _appTables.GetByIdAsync(40, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 40, Name = "Clients copy" });

        var users = Substitute.For<IUserRepository>();
        var verifier = new ConnectedAccessVerifier(_control, _connections, Substitute.For<IUserTokenRepository>(),
            new ConnectedSourceScopeFactory(Substitute.For<IServiceScopeFactory>()));
        var notifier = new ConnectedNotifier(_email, users, Substitute.For<IAppRepository>(), NullLogger<ConnectedNotifier>.Instance);
        _processor = new ConnectedSyncProcessor(_runs, _tables, _connections, _control, _writer, _lock, verifier, _appTables,
            Substitute.For<IAppFieldRepository>(), users, _audit, _context, notifier, Options.Create(new ConnectedTablesOptions { FailureThreshold = 3 }),
            NullLogger<ConnectedSyncProcessor>.Instance);
    }

    [Fact]
    public async Task Run_AlreadyFinished_DoesNothing()
    {
        _run.Status = ConnectedRunStatuses.Success;
        (await _processor.RunAsync(_run.PublicId, 1, CancellationToken.None)).Should().Be(ConnectedRunOutcome.Done);
        await _lock.DidNotReceive().TryAcquireAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_TableBusy_IsRetriedLater()
    {
        _lock.TryAcquireAsync(4, Arg.Any<CancellationToken>()).Returns((IAsyncDisposable?)null);
        (await _processor.RunAsync(_run.PublicId, 1, CancellationToken.None)).Should().Be(ConnectedRunOutcome.RetryLater);
        await _runs.DidNotReceive().CompleteAsync(Arg.Any<ConnectedRun>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_DestinationDeleted_MarksTableRemoved()
    {
        _appTables.GetByIdAsync(40, Arg.Any<CancellationToken>()).Throws(new NotFoundException("AppTable", 40));

        await _processor.RunAsync(_run.PublicId, 1, CancellationToken.None);

        await _tables.Received().SoftDeleteAsync(4, ConnectedTableStatuses.Removed, Arg.Any<CancellationToken>());
        await _runs.Received().CompleteAsync(_run, ConnectedRunStatuses.Failed, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_RevokedConnection_FailsAndNeedsAttention_WithoutReading()
    {
        _connections.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new ConnectedConnection
        {
            Id = 9, Name = "c", Status = ConnectedConnectionStatuses.Revoked, AuthMode = ConnectedAuthModes.UserToken,
        });

        await _processor.RunAsync(_run.PublicId, 1, CancellationToken.None);

        await _runs.Received().CompleteAsync(_run, ConnectedRunStatuses.Failed, Arg.Is<string?>(m => m!.Contains("revoked")), Arg.Any<CancellationToken>());
        await _tables.Received().SetStatusAsync(4, ConnectedTableStatuses.NeedsAttention, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _runs.DidNotReceive().MarkRunningAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _writer.DidNotReceiveWithAnyArgs().WriteChunkAsync(default!, default!, default, default, default!, default!, default!, default);
    }

    [Fact]
    public async Task Run_ProviderRevokedThisTable_Fails()
    {
        _control.GetLinkAsync(1, _table.PublicId, Arg.Any<CancellationToken>())
            .Returns(new ConnectedLink { Status = ConnectedLinkStatuses.RevokedByProvider });
        _connections.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(new ConnectedConnection { Id = 9, Status = ConnectedConnectionStatuses.Active });

        await _processor.RunAsync(_run.PublicId, 1, CancellationToken.None);

        await _runs.Received().CompleteAsync(_run, ConnectedRunStatuses.Failed, Arg.Is<string?>(m => m!.Contains("stopped")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_RepeatedScheduledFailures_SwitchScheduleToManual()
    {
        _connections.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns((ConnectedConnection?)null);
        _tables.RecordFailureAsync(4, Arg.Any<CancellationToken>()).Returns(3);

        await _processor.RunAsync(_run.PublicId, 1, CancellationToken.None);

        await _tables.Received().DisableScheduleAsync(4, Arg.Is<string>(json => ConnectedScheduling.Parse(json)!.Enabled == false), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_InterruptedTooOften_Fails()
    {
        _run.Status = ConnectedRunStatuses.Running;

        await _processor.RunAsync(_run.PublicId, attempt: 10, CancellationToken.None);

        await _runs.Received().CompleteAsync(_run, ConnectedRunStatuses.Failed, Arg.Is<string?>(m => m!.Contains("interrupted")), Arg.Any<CancellationToken>());
        await _runs.DidNotReceive().ResetToQueuedAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsTransient_RecognisesTimeoutsAndWrappedOnes()
    {
        ConnectedSyncProcessor.IsTransient(new TimeoutException()).Should().BeTrue();
        ConnectedSyncProcessor.IsTransient(new InvalidOperationException("x", new TimeoutException())).Should().BeTrue();
        ConnectedSyncProcessor.IsTransient(new InvalidOperationException("x")).Should().BeFalse();
    }
}
