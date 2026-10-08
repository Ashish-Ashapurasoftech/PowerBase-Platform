using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>The history of an app's imports and the downloads that go with it.</summary>
public class ImportHistoryTests
{
    private static readonly Guid AppId = Guid.NewGuid();
    private static readonly Guid TableA = Guid.NewGuid();

    private readonly IAppRepository _apps = Substitute.For<IAppRepository>();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IImportRunRepository _runs = Substitute.For<IImportRunRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IQueryContext _user = Substitute.For<IQueryContext>();
    private ImportHistoryQuery? _asked;

    public ImportHistoryTests()
    {
        _apps.GetIdByPublicIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(7L);
        _user.UserId.Returns(5L);
        _runs.ListHistoryAsync(Arg.Do<ImportHistoryQuery>(q => _asked = q), Arg.Any<CancellationToken>()).Returns([]);
        _users.GetNamesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<long, string> { [5] = "Prashant", [6] = "Kuldeep" });
    }

    private ListImportHistoryHandler Sut(int days = 30) => new(_apps, _access, _runs, _users, _user, Options.Create(new ImportOptions { RetentionDays = days }));

    private static ImportHistoryRow Row(long by = 5, DateTime? completed = null, bool details = true, string? file = null, DateTime? expiredOn = null, int total = 1) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Nightly", TableA, "People", 1, file is null ? "table" : "file", file, "success", "manual", by,
        (completed ?? DateTime.UtcNow).AddMinutes(-3), (completed ?? DateTime.UtcNow).AddMinutes(-2), completed ?? DateTime.UtcNow,
        100, 90, 5, 2, 2, 1, "memo", details, expiredOn, total);

    private Task<ImportHistoryPage> List(DateTime? from = null, DateTime? to = null, string? status = null, string? trigger = null, int page = 1, int size = 25)
        => Sut().HandleAsync(AppId, from, to, null, null, status, trigger, page, size, default);

    // ---- who may look, and the period ----

    [Fact]
    public async Task Only_members_of_the_app_see_its_history()
    {
        _access.RequireMembershipByAppPublicIdAsync(AppId, Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new UnauthorizedActionException("see this app"));

        var act = () => List();

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await _runs.DidNotReceiveWithAnyArgs().ListHistoryAsync(default!, default);
    }

    [Fact]
    public async Task Without_a_period_it_is_the_last_thirty_days_of_this_app()
    {
        await List();

        _asked!.AppId.Should().Be(7);
        _asked.From.Should().BeCloseTo(DateTime.UtcNow.AddDays(-30), TimeSpan.FromSeconds(5));
        _asked.To.Should().BeNull();
    }

    [Fact]
    public async Task A_period_is_passed_on_as_asked_but_never_reaches_back_further_than_the_limit()
    {
        await List(from: DateTime.UtcNow.AddDays(-3000));

        _asked!.From.Should().BeCloseTo(DateTime.UtcNow.AddDays(-ListImportHistoryHandler.MaxDays), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        var act = () => List(from: new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), to: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Theory]
    [InlineData("nonsense", null)]
    [InlineData(null, "carrier-pigeon")]
    public async Task A_filter_value_that_is_not_one_of_the_known_ones_is_refused(string? status, string? trigger)
    {
        var act = () => List(status: status, trigger: trigger);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Filters_are_passed_on_in_their_canonical_form()
    {
        await List(status: " PARTIAL ", trigger: "Schedule");

        _asked!.Status.Should().Be("partial");
        _asked.Trigger.Should().Be("schedule");
    }

    // ---- paging ----

    [Theory]
    [InlineData(1, 25, 0, 25)]
    [InlineData(3, 10, 20, 10)]
    [InlineData(0, 0, 0, 25)]
    [InlineData(-4, 5000, 0, 100)]
    public async Task The_page_asked_for_is_read_and_a_huge_page_is_capped(int page, int size, int skip, int take)
    {
        await List(page: page, size: size);

        (_asked!.Skip, _asked.Take).Should().Be((skip, take));
    }

    [Fact]
    public async Task The_total_comes_with_the_first_row_and_is_zero_for_an_empty_page()
    {
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(total: 83)]);
        (await List()).Total.Should().Be(83);

        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([]);
        (await List()).Total.Should().Be(0);
    }

    // ---- what a row shows ----

    [Fact]
    public async Task Names_are_looked_up_once_for_everyone_on_the_page()
    {
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(by: 5), Row(by: 6), Row(by: 5)]);

        var page = await List();

        page.Items.Select(i => i.StartedBy).Should().Equal("Prashant", "Kuldeep", "Prashant");
        await _users.Received(1).GetNamesByIdsAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_row_shows_how_long_the_run_took_and_when_its_files_go()
    {
        var done = DateTime.UtcNow.AddDays(-2);
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(completed: done)]);

        var item = (await List()).Items.Single();

        item.DurationSeconds.Should().Be(120);
        item.FilesExpireOn.Should().BeCloseTo(done.AddDays(30), TimeSpan.FromSeconds(1));
        item.FilesExpired.Should().BeFalse();
        item.Memo.Should().Be("memo");
    }

    [Fact]
    public async Task The_person_who_started_the_run_can_download_its_files_and_another_member_cannot()
    {
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(by: 5, file: "people.xlsx"), Row(by: 6, file: "people.xlsx")]);

        var items = (await List()).Items;

        items[0].HasDetails.Should().BeTrue();
        items[0].HasSourceFile.Should().BeTrue();
        items[1].HasDetails.Should().BeFalse("it carries another person's source values");
        items[1].HasSourceFile.Should().BeFalse();
        items[1].RowsRead.Should().Be(100, "but the counts are for every member");
    }

    [Fact]
    public async Task An_admin_can_download_anyones()
    {
        _user.IsTenantAdmin.Returns(true);
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(by: 6, file: "p.csv")]);

        var item = (await List()).Items.Single();

        (item.HasDetails, item.HasSourceFile).Should().Be((true, true));
    }

    [Fact]
    public async Task Past_the_retention_the_files_are_expired_even_before_the_clean_up_has_got_to_them()
    {
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(completed: DateTime.UtcNow.AddDays(-31), file: "p.csv")]);

        var item = (await List()).Items.Single();

        item.FilesExpired.Should().BeTrue();
        (item.HasDetails, item.HasSourceFile).Should().Be((false, false));
        item.RowsRead.Should().Be(100, "the counts stay");
    }

    [Fact]
    public async Task Files_the_clean_up_has_deleted_are_expired_whatever_the_date()
    {
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(expiredOn: DateTime.UtcNow)]);

        (await List()).Items.Single().FilesExpired.Should().BeTrue();
    }

    [Fact]
    public async Task A_run_that_kept_no_files_is_not_called_expired()
    {
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(completed: DateTime.UtcNow.AddDays(-90), details: false)]);

        (await List()).Items.Single().FilesExpired.Should().BeFalse();
    }

    [Fact]
    public async Task A_run_still_going_has_no_end_duration_or_expiry()
    {
        var running = Row() with { CompletedOn = null, Status = "running" };
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([running]);

        var item = (await List()).Items.Single();

        (item.DurationSeconds, item.FilesExpireOn, item.FilesExpired).Should().Be(((double?)null, (DateTime?)null, false));
    }

    [Fact]
    public async Task The_page_says_how_many_days_files_are_kept()
    {
        (await Sut(days: 14).HandleAsync(AppId, null, null, null, null, null, null, 1, 25, default)).RetentionDays.Should().Be(14);
    }

    [Fact]
    public async Task The_retention_that_is_configured_is_the_one_used()
    {
        var done = DateTime.UtcNow.AddDays(-10);
        _runs.ListHistoryAsync(Arg.Any<ImportHistoryQuery>(), Arg.Any<CancellationToken>()).Returns([Row(completed: done)]);

        var item = (await Sut(days: 7).HandleAsync(AppId, null, null, null, null, null, null, 1, 25, default)).Items.Single();

        item.FilesExpired.Should().BeTrue("seven days have passed");
    }

    // ---- downloads ----

    private static readonly Guid RunId = Guid.NewGuid();

    private IAppAccessService Access => _access;

    private ImportRun StoredRun(Action<ImportRun>? tweak = null, ImportRunFile? file = null, bool multi = false)
    {
        var cfg = new ImportDefinitionConfig { Name = "n" };
        if (multi) cfg.AdditionalTargets = [new ImportTargetConfig { DestinationTableId = Guid.NewGuid() }];
        var run = new ImportRun
        {
            Id = 3, PublicId = RunId, TriggeredByUserId = 5, CompletedOn = DateTime.UtcNow, FeedbackFileUrl = "/d/run.csv",
            DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(TableA, cfg, null, file))
        };
        tweak?.Invoke(run);
        _runs.GetByPublicIdAsync(RunId, Arg.Any<CancellationToken>()).Returns(run);
        return run;
    }

    private IFileStorageService Storage(string path, string content = "data")
    {
        var storage = Substitute.For<IFileStorageService, IFileStorageReadService>();
        ((IFileStorageReadService)storage).OpenReadAsync(path, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))));
        return storage;
    }

    [Fact]
    public async Task The_source_file_is_streamed_to_the_person_who_started_the_run()
    {
        StoredRun(file: new ImportRunFile(Guid.NewGuid(), "/src/people.xlsx", "people.xlsx"));

        var file = await new GetImportSourceFileHandler(Access, _runs, Storage("/src/people.xlsx"), _user).HandleAsync(RunId, default);

        file.FileName.Should().Be("people.xlsx");
        using var reader = new StreamReader(file.Content);
        (await reader.ReadToEndAsync()).Should().Be("data");
    }

    [Fact]
    public async Task Another_member_cannot_download_the_source_file()
    {
        StoredRun(r => r.TriggeredByUserId = 6, new ImportRunFile(Guid.NewGuid(), "/src/p.csv", "p.csv"));

        var act = () => new GetImportSourceFileHandler(Access, _runs, Storage("/src/p.csv"), _user).HandleAsync(RunId, default);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task A_source_file_past_its_retention_or_a_run_with_no_file_is_not_found()
    {
        StoredRun(r => r.FilesExpiredOn = DateTime.UtcNow, new ImportRunFile(Guid.NewGuid(), "/src/p.csv", "p.csv"));
        var expired = () => new GetImportSourceFileHandler(Access, _runs, Storage("/src/p.csv"), _user).HandleAsync(RunId, default);
        await expired.Should().ThrowAsync<NotFoundException>();

        StoredRun();
        var none = () => new GetImportSourceFileHandler(Access, _runs, Storage("/x"), _user).HandleAsync(RunId, default);
        await none.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_source_file_the_storage_no_longer_has_is_not_found()
    {
        StoredRun(file: new ImportRunFile(Guid.NewGuid(), "/src/p.csv", "p.csv"));
        var storage = Substitute.For<IFileStorageService, IFileStorageReadService>();
        ((IFileStorageReadService)storage).OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<Stream>>(_ => throw new FileNotFoundException());

        var act = () => new GetImportSourceFileHandler(Access, _runs, storage, _user).HandleAsync(RunId, default);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_tables_details_file_is_the_one_downloaded_for_a_run_that_filled_several()
    {
        StoredRun(multi: true);
        var contacts = Guid.NewGuid();
        _runs.GetTargetDetailsAsync(3, contacts, Arg.Any<CancellationToken>()).Returns(("/d/run-1.csv", "Contacts & Co"));

        var file = await new GetImportFeedbackHandler(Access, _runs, Storage("/d/run-1.csv", "contacts"), _user).HandleAsync(RunId, default, contacts);

        using var reader = new StreamReader(file.Content);
        (await reader.ReadToEndAsync()).Should().Be("contacts");
        file.FileName.Should().StartWith("import-details-ContactsCo-").And.EndWith(".csv", "the table's name is made safe for a file name");
    }

    [Fact]
    public async Task Without_a_table_the_first_tables_file_is_downloaded_as_before()
    {
        StoredRun(multi: true);

        var file = await new GetImportFeedbackHandler(Access, _runs, Storage("/d/run.csv", "first"), _user).HandleAsync(RunId, default);

        using var reader = new StreamReader(file.Content);
        (await reader.ReadToEndAsync()).Should().Be("first");
    }

    [Fact]
    public async Task A_table_that_has_no_details_file_for_the_run_is_not_found()
    {
        StoredRun(multi: true);
        _runs.GetTargetDetailsAsync(Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ValueTuple<string, string>?)null);

        var act = () => new GetImportFeedbackHandler(Access, _runs, Storage("/d/run.csv"), _user).HandleAsync(RunId, default, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Details_past_their_retention_are_not_found_even_if_the_path_is_still_recorded()
    {
        StoredRun(r => r.FilesExpiredOn = DateTime.UtcNow);

        var act = () => new GetImportFeedbackHandler(Access, _runs, Storage("/d/run.csv"), _user).HandleAsync(RunId, default);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_run_page_says_there_is_a_source_file_only_to_those_who_may_download_it_and_only_while_it_is_kept()
    {
        StoredRun(file: new ImportRunFile(Guid.NewGuid(), "/s.csv", "s.csv"));
        var handler = new GetImportRunHandler(Access, _runs, _user);

        var mine = await handler.HandleAsync(RunId, default);
        mine.SourceFileName.Should().Be("s.csv");
        mine.HasSourceFile.Should().BeTrue();

        StoredRun(r => r.TriggeredByUserId = 6, new ImportRunFile(Guid.NewGuid(), "/s.csv", "s.csv"));
        var other = await handler.HandleAsync(RunId, default);
        other.HasSourceFile.Should().BeFalse();

        StoredRun(r => r.FilesExpiredOn = DateTime.UtcNow, new ImportRunFile(Guid.NewGuid(), "/s.csv", "s.csv"));
        var expired = await handler.HandleAsync(RunId, default);
        (expired.HasSourceFile, expired.HasFeedback, expired.FilesExpired).Should().Be((false, false, true));
    }
}
