using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>The details files and the uploaded source file of a run are kept for the configured retention, then deleted; the run stays.</summary>
public class ImportRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly IImportRunRepository _runs = Substitute.For<IImportRunRepository>();
    private readonly IImportFileRepository _files = Substitute.For<IImportFileRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();

    private ImportRetentionCleanup Sut(int days = 30) =>
        new(_runs, _files, _storage, Options.Create(new ImportOptions { RetentionDays = days }), NullLogger<ImportRetentionCleanup>.Instance);

    private static ImportExpiredFiles Expired(long id, string? feedback, string[]? targets = null, Guid? fileId = null, string? filePath = null)
    {
        var snapshot = new ImportRunSnapshot(Guid.NewGuid(), new ImportDefinitionConfig { Name = "n" }, null,
            fileId is null ? null : new ImportRunFile(fileId.Value, filePath ?? "/src.csv", "src.csv"));
        return new ImportExpiredFiles(id, feedback, ImportJson.Serialize(snapshot), targets ?? []);
    }

    // ---- the setting ----

    [Theory]
    [InlineData(30, 30)]
    [InlineData(7, 7)]
    [InlineData(365, 365)]
    [InlineData(1, 1)]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(366, 30)]
    [InlineData(100000, 30)]
    public void The_retention_is_the_configured_days_and_never_zero_or_for_ever(int configured, int effective)
        => new ImportOptions { RetentionDays = configured }.EffectiveRetentionDays.Should().Be(effective);

    [Fact]
    public void Without_a_setting_the_retention_is_thirty_days() => new ImportOptions().EffectiveRetentionDays.Should().Be(30);

    [Fact]
    public void The_section_is_called_Imports_in_the_configuration() => ImportOptions.SectionName.Should().Be("Imports");

    // ---- the clean-up ----

    [Fact]
    public async Task Runs_older_than_the_retention_lose_their_files_and_are_marked()
    {
        var fileId = Guid.NewGuid();
        _runs.ListWithExpiredFilesAsync(Now.AddDays(-30), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Expired(1, "/details-0.csv", ["/details-0.csv", "/details-1.csv"], fileId, "/src.csv")]);

        var count = await Sut().RunAsync(Now, default);

        count.Should().Be(1);
        foreach (var path in new[] { "/details-0.csv", "/details-1.csv", "/src.csv" }) await _storage.Received(1).DeleteAsync(path, Arg.Any<CancellationToken>());
        await _files.Received(1).DeleteAsync(fileId, Arg.Any<CancellationToken>());
        await _runs.Received(1).MarkFilesExpiredAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_configured_days_decide_which_runs_are_old_enough()
    {
        await Sut(7).RunAsync(Now, default);

        await _runs.Received(1).ListWithExpiredFilesAsync(Now.AddDays(-7), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_now_leaves_the_run_for_the_next_pass()
    {
        _runs.ListWithExpiredFilesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Expired(1, "/a.csv", ["/a.csv"])]);
        _storage.DeleteAsync("/a.csv", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("storage down"));

        var count = await Sut().RunAsync(Now, default);

        count.Should().Be(0);
        await _runs.DidNotReceiveWithAnyArgs().MarkFilesExpiredAsync(default, default);
    }

    [Fact]
    public async Task One_run_that_cannot_be_cleaned_does_not_stop_the_next()
    {
        _runs.ListWithExpiredFilesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Expired(1, "/bad.csv"), Expired(2, "/good.csv")]);
        _storage.DeleteAsync("/bad.csv", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("nope"));

        var count = await Sut().RunAsync(Now, default);

        count.Should().Be(1);
        await _runs.Received(1).MarkFilesExpiredAsync(2, Arg.Any<CancellationToken>());
        await _runs.DidNotReceive().MarkFilesExpiredAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_that_is_already_gone_counts_as_deleted()
    {
        _runs.ListWithExpiredFilesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Expired(1, "/gone.csv")]);
        _storage.DeleteAsync("/gone.csv", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new FileNotFoundException());

        (await Sut().RunAsync(Now, default)).Should().Be(1);
    }

    [Fact]
    public async Task A_run_that_kept_no_files_is_marked_all_the_same_so_it_is_not_looked_at_again()
    {
        _runs.ListWithExpiredFilesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Expired(1, null)]);

        await Sut().RunAsync(Now, default);

        await _storage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        await _runs.Received(1).MarkFilesExpiredAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_same_path_is_deleted_once_even_when_the_run_and_its_first_table_share_it()
    {
        _runs.ListWithExpiredFilesAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Expired(1, "/same.csv", ["/same.csv"])]);

        await Sut().RunAsync(Now, default);

        await _storage.Received(1).DeleteAsync("/same.csv", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_kept_upload_whose_retention_ended_with_no_run_left_is_deleted_too()
    {
        var orphan = new ImportFile { PublicId = Guid.NewGuid(), StoragePath = "/orphan.csv" };
        _files.ListRetentionEndedAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([orphan]);

        await Sut().RunAsync(Now, default);

        await _storage.Received(1).DeleteAsync("/orphan.csv", Arg.Any<CancellationToken>());
        await _files.Received(1).DeleteAsync(orphan.PublicId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_old_means_nothing_is_touched()
    {
        (await Sut().RunAsync(Now, default)).Should().Be(0);

        await _storage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }
}
