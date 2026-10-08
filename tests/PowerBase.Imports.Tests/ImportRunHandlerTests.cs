using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>Starting a run with recipients, stopping a run, and listing the user's own runs.</summary>
public class ImportRunHandlerTests
{
    private const long Starter = 5;
    private readonly IImportRunRepository _runs = Substitute.For<IImportRunRepository>();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IAuditRepository _audit = Substitute.For<IAuditRepository>();

    private static IQueryContext User(long id, bool superAdmin = false, bool tenantAdmin = false)
    {
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(id);
        user.TenantId.Returns(1);
        user.IsSuperAdmin.Returns(superAdmin);
        user.IsTenantAdmin.Returns(tenantAdmin);
        return user;
    }

    // ---- starting a run ----

    private sealed record Started(ImportRun Run, long TenantId, Guid QueuedRun);

    private async Task<(ImportRun Run, ImportRunSnapshot Snapshot)> Start(List<string>? saved, IReadOnlyList<string>? overrideWith, string? baseUrl)
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "Nightly", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy, NotifyEmails = saved ?? [],
            Mappings = [new() { DestFid = 6, SourceFid = 6 }]
        });
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 1, PublicId = ImportHarness.DestTableId });
        tables.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10, AppId = 1, PublicId = ImportHarness.SourceTableId });
        var definitions = Substitute.For<IImportDefinitionRepository>();
        var definition = new ImportDefinition
        {
            Id = 1, PublicId = Guid.NewGuid(), DestinationTableId = 11, SourceTableId = 10, Name = "Nightly", ImportType = "copy",
            FieldMappingJson = ImportJson.Serialize(new List<ImportFieldMapping> { new() { DestFid = 6, SourceFid = 6 } })
        };
        ImportConfigMapper.Apply(definition, new ImportDefinitionConfig
        {
            Name = "Nightly", SourceTableId = ImportHarness.SourceTableId, Mappings = [new() { DestFid = 6, SourceFid = 6 }], NotifyEmails = saved ?? []
        });
        definitions.GetByPublicIdAsync(definition.PublicId, Arg.Any<CancellationToken>()).Returns(definition);
        ImportRun? created = null;
        _runs.When(r => r.CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>())).Do(c => { created = c.ArgAt<ImportRun>(0); created.PublicId = Guid.NewGuid(); });
        var handler = new StartImportRunHandler(h.PlanBuilder, tables, Substitute.For<IAppRepository>(), definitions, _runs, Substitute.For<IImportQueue>(), User(Starter), Substitute.For<IImportDefinitionChecker>(), Substitute.For<IImportFileRepository>(), Substitute.For<PowerBase.Application.Imports.Files.IImportFileAccess>());

        await handler.HandleAsync(definition.PublicId, ImportTrigger.Manual, overrideWith, baseUrl, default);

        return (created!, ImportJson.Deserialize<ImportRunSnapshot>(created!.DefinitionSnapshotJson)!);
    }

    [Fact]
    public async Task A_run_uses_the_people_saved_with_the_import_unless_this_run_names_others()
    {
        var saved = await Start(["saved@example.com"], null, null);
        var overridden = await Start(["saved@example.com"], ["other@example.com", "OTHER@example.com"], null);
        var nobody = await Start(["saved@example.com"], [], null);

        saved.Snapshot.Config.NotifyEmails.Should().Equal("saved@example.com");
        overridden.Snapshot.Config.NotifyEmails.Should().Equal("other@example.com");
        nobody.Snapshot.Config.NotifyEmails.Should().BeEmpty("an empty list for this run means nobody besides the starter");
    }

    [Fact]
    public async Task The_run_remembers_the_link_address_it_was_started_from()
    {
        var started = await Start(null, null, "https://app.example.com");

        started.Snapshot.FrontendBaseUrl.Should().Be("https://app.example.com");
        started.Run.TriggeredByUserId.Should().Be(Starter);
    }

    [Fact]
    public async Task A_bad_address_for_one_run_is_refused_before_anything_is_queued()
    {
        var act = () => Start(null, ["nonsense"], null);

        await act.Should().ThrowAsync<ValidationException>();
        await _runs.DidNotReceive().CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>());
    }

    // ---- stopping a run ----

    private ImportRun ActiveRun()
    {
        var run = new ImportRun
        {
            Id = 7, PublicId = Guid.NewGuid(), TriggeredByUserId = Starter, Status = ImportRunStatus.Running,
            DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(Guid.NewGuid(), new ImportDefinitionConfig()))
        };
        _runs.GetByPublicIdAsync(run.PublicId, Arg.Any<CancellationToken>()).Returns(run);
        return run;
    }

    private CancelImportRunHandler Cancel(IQueryContext user) => new(_access, _runs, user, _audit);

    [Theory]
    [InlineData(ImportCancelOutcome.Cancelled)]
    [InlineData(ImportCancelOutcome.Requested)]
    public async Task The_person_who_started_a_run_can_stop_it(ImportCancelOutcome outcome)
    {
        var run = ActiveRun();
        _runs.RequestCancelAsync(7, Arg.Any<CancellationToken>()).Returns(outcome);

        (await Cancel(User(Starter)).HandleAsync(run.PublicId, default)).Should().Be(outcome);

        await _audit.Received(1).LogActivityAsync("ImportCancelRequested", "ImportRun", run.PublicId.ToString(), Arg.Any<string?>(),
            Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task An_admin_can_stop_someone_elses_run(bool superAdmin, bool tenantAdmin)
    {
        var run = ActiveRun();
        _runs.RequestCancelAsync(7, Arg.Any<CancellationToken>()).Returns(ImportCancelOutcome.Requested);

        var act = () => Cancel(User(99, superAdmin, tenantAdmin)).HandleAsync(run.PublicId, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Another_member_of_the_app_cannot_stop_the_run()
    {
        var run = ActiveRun();

        var act = () => Cancel(User(99)).HandleAsync(run.PublicId, default);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await _runs.DidNotReceive().RequestCancelAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stopping_a_run_that_has_already_finished_is_a_clear_conflict_not_a_silent_success()
    {
        var run = ActiveRun();
        _runs.RequestCancelAsync(7, Arg.Any<CancellationToken>()).Returns(ImportCancelOutcome.NotActive);

        var act = () => Cancel(User(Starter)).HandleAsync(run.PublicId, default);

        await act.Should().ThrowAsync<ConflictException>().WithMessage("*already finished*");
    }

    // ---- the user's own runs ----

    [Fact]
    public async Task The_notice_list_is_the_current_users_own_and_returns_the_clock_to_ask_again_with()
    {
        var serverTime = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);
        _runs.ListForUserAsync(Starter, null, 20, Arg.Any<CancellationToken>()).Returns((serverTime, (IReadOnlyList<ImportRunNotice>)new List<ImportRunNotice>()));

        var notices = await new ListMyImportRunsHandler(_runs, User(Starter)).HandleAsync(null, default);

        notices.ServerTime.Should().Be(serverTime);
        await _runs.Received(1).ListForUserAsync(Starter, null, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_browser_that_was_closed_for_days_only_hears_about_the_last_hour()
    {
        DateTime? asked = null;
        _runs.ListForUserAsync(Starter, Arg.Do<DateTime?>(d => asked = d), 20, Arg.Any<CancellationToken>())
            .Returns((DateTime.UtcNow, (IReadOnlyList<ImportRunNotice>)new List<ImportRunNotice>()));

        await new ListMyImportRunsHandler(_runs, User(Starter)).HandleAsync(DateTime.UtcNow.AddDays(-3), default);

        asked.Should().BeAfter(DateTime.UtcNow.AddMinutes(-61)).And.BeBefore(DateTime.UtcNow.AddMinutes(-59));
    }

    [Fact]
    public async Task A_recent_since_is_passed_through_unchanged()
    {
        var since = DateTime.UtcNow.AddMinutes(-2);
        DateTime? asked = null;
        _runs.ListForUserAsync(Starter, Arg.Do<DateTime?>(d => asked = d), 20, Arg.Any<CancellationToken>())
            .Returns((DateTime.UtcNow, (IReadOnlyList<ImportRunNotice>)new List<ImportRunNotice>()));

        await new ListMyImportRunsHandler(_runs, User(Starter)).HandleAsync(since, default);

        asked.Should().Be(since);
    }
}
