using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>The run API: starting through the app route, retrying with a client token, and reading a run's status.</summary>
public class ImportRunApiTests
{
    private static readonly Guid AppPublicId = Guid.NewGuid();
    private readonly IImportRunRepository _runs = Substitute.For<IImportRunRepository>();
    private readonly IImportQueue _queue = Substitute.For<IImportQueue>();
    private readonly IAppRepository _apps = Substitute.For<IAppRepository>();
    private readonly ImportDefinition _definition;
    private readonly StartImportRunHandler _sut;

    public ImportRunApiTests()
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "Nightly", SourceTableId = ImportHarness.SourceTableId, Mappings = [new() { DestFid = 6, SourceFid = 6 }]
        });
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 1, PublicId = ImportHarness.DestTableId });
        tables.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10, AppId = 1, PublicId = ImportHarness.SourceTableId });
        _apps.GetIdByPublicIdAsync(AppPublicId, Arg.Any<CancellationToken>()).Returns(1L);
        _definition = new ImportDefinition { Id = 1, PublicId = Guid.NewGuid(), DestinationTableId = 11, SourceTableId = 10, Name = "Nightly", ImportType = "copy" };
        ImportConfigMapper.Apply(_definition, new ImportDefinitionConfig
        {
            Name = "Nightly", SourceTableId = ImportHarness.SourceTableId, Mappings = [new() { DestFid = 6, SourceFid = 6 }]
        });
        var definitions = Substitute.For<IImportDefinitionRepository>();
        definitions.GetByPublicIdAsync(_definition.PublicId, Arg.Any<CancellationToken>()).Returns(_definition);
        _runs.CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>()).Returns(ci => { var r = ci.Arg<ImportRun>(); r.Id = 9; r.PublicId = Guid.NewGuid(); return 9L; });
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(5L);
        user.TenantId.Returns(1L);
        _sut = new StartImportRunHandler(h.PlanBuilder, tables, _apps, definitions, _runs, _queue, user, Substitute.For<IImportDefinitionChecker>(), Substitute.For<IImportFileRepository>(), Substitute.For<PowerBase.Application.Imports.Files.IImportFileAccess>());
    }

    private Task<ImportRunStart> Start(Guid? app = null, string? token = null) =>
        _sut.StartAsync(_definition.PublicId, ImportTrigger.Api, null, null, app ?? AppPublicId, token, null, default);

    [Fact]
    public async Task A_run_started_through_the_api_is_marked_as_an_api_run_and_queued()
    {
        var started = await Start();

        started.Replayed.Should().BeFalse();
        await _runs.Received(1).CreateAsync(Arg.Is<ImportRun>(r => r.TriggeredBy == "api" && r.IdempotencyKey == null), Arg.Any<CancellationToken>());
        await _queue.Received(1).EnqueueAsync(1, started.RunId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_import_of_another_app_is_not_found_and_nothing_is_started()
    {
        _apps.GetIdByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(2L);

        var act = () => Start();

        await act.Should().ThrowAsync<NotFoundException>();
        await _runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task The_client_token_is_stored_as_a_digest_not_as_sent()
    {
        await Start(token: "nightly-2026-10-01");

        await _runs.Received(1).CreateAsync(
            Arg.Is<ImportRun>(r => r.IdempotencyKey != null && r.IdempotencyKey.Length == 64 && !r.IdempotencyKey.Contains("nightly")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repeating_a_token_returns_the_first_run_even_if_it_is_still_going_and_starts_nothing()
    {
        var first = Guid.NewGuid();
        _runs.FindPublicIdByKeyAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(first);
        _runs.HasActiveRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var again = await Start(token: "t1");

        again.Should().Be(new ImportRunStart(first, true));
        await _runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
        await _queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default, default);
    }

    [Fact]
    public async Task Two_requests_racing_with_one_token_start_one_run()
    {
        // The second request passed the lookup before the first inserted, then lost at the insert (0 = key already used).
        var winner = Guid.NewGuid();
        _runs.FindPublicIdByKeyAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Guid?)null, winner);
        _runs.CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>()).Returns(0L);

        var result = await Start(token: "t1");

        result.Should().Be(new ImportRunStart(winner, true));
        await _queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default, default);
    }

    [Fact]
    public async Task Without_a_token_a_second_start_while_running_is_refused()
    {
        _runs.HasActiveRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var act = () => Start();

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_token_means_no_token(string? token) => ImportRunKey.From(token).Should().BeNull();

    [Fact]
    public void Tokens_are_trimmed_and_hash_consistently()
        => ImportRunKey.From(" abc ").Should().Be(ImportRunKey.From("abc")).And.NotBe(ImportRunKey.From("abd"));

    [Fact]
    public void An_overlong_token_is_refused()
    {
        var act = () => ImportRunKey.From(new string('x', ImportRunKey.MaxTokenLength + 1));
        act.Should().Throw<ValidationException>();
    }

    // ---- status ----

    private (GetImportRunStatusHandler Handler, ImportRun Run) StatusFor(long startedBy, long caller, bool admin = false, long runAppId = 1)
    {
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByPublicIdAsync(ImportHarness.DestTableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = runAppId, PublicId = ImportHarness.DestTableId });
        var run = new ImportRun
        {
            Id = 3, PublicId = Guid.NewGuid(), Status = "partial", RowsRead = 10, Inserted = 8, Errored = 2, FeedbackFileUrl = "files/x.csv", TriggeredByUserId = startedBy,
            DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(ImportHarness.DestTableId, new ImportDefinitionConfig { Name = "Nightly" }, null))
        };
        _runs.GetByPublicIdAsync(run.PublicId, Arg.Any<CancellationToken>()).Returns(run);
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(caller);
        user.IsTenantAdmin.Returns(admin);
        return (new GetImportRunStatusHandler(Substitute.For<IAppAccessService>(), _runs, tables, _apps, user), run);
    }

    [Fact]
    public async Task Status_reports_counts_and_that_a_details_file_exists_without_the_path()
    {
        var (handler, run) = StatusFor(5, 5);

        var status = await handler.HandleAsync(AppPublicId, run.PublicId, default);

        status.Should().BeEquivalentTo(new { Status = "partial", RowsRead = 10L, Inserted = 8L, Errored = 2L, HasFeedback = true });
        typeof(ImportRunStatusResponse).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Url") || n.Contains("Path") || n.Contains("Snapshot"));
    }

    [Fact]
    public async Task Another_member_sees_the_status_but_is_not_offered_the_details_file()
    {
        var (handler, run) = StatusFor(startedBy: 5, caller: 6);

        (await handler.HandleAsync(AppPublicId, run.PublicId, default)).HasFeedback.Should().BeFalse();
    }

    [Fact]
    public async Task A_run_asked_for_through_the_wrong_app_is_not_found()
    {
        var (handler, run) = StatusFor(5, 5, runAppId: 2);

        var act = () => handler.HandleAsync(AppPublicId, run.PublicId, default);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
