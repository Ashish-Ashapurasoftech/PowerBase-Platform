using System.Globalization;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>How a run ends: stopped by a user at any stage, cancelled while still queued, and the notification every ending
/// sends.</summary>
public class ImportRunLifecycleTests
{
    private static readonly Guid Source = ImportHarness.SourceTableId;

    private static IReadOnlyDictionary<string, object?> Clean(long id, string? qty = null) => new Dictionary<string, object?>
    {
        ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = qty ?? id.ToString(CultureInfo.InvariantCulture), ["f_9"] = "x"
    };

    private static ImportDefinitionConfig Copy(string policy = ImportConstraintPolicy.ImportValid, List<string>? notify = null) => new()
    {
        Name = "Nightly", SourceTableId = Source, ImportType = ImportTypes.Copy, ConstraintPolicy = policy, NotifyEmails = notify ?? [],
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }]
    };

    // ---- stopping a run ----

    [Fact]
    public async Task A_run_stopped_by_the_user_ends_after_the_chunk_it_was_on_and_keeps_what_it_imported()
    {
        var h = ImportHarness.Create(Copy(), new HarnessOptions { Row = id => Clean(id), CancelAtAdvance = 1 });

        await h.RunAsync();

        h.Advances.Should().Be(1, "it stops at the first chunk it records, without reading another");
        h.Store.Inserted.Should().HaveCount(ImportSourceReader.ChunkSize);
        h.Counters.Read.Should().Be(ImportSourceReader.ChunkSize);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Cancelled);
        h.Completion!.Value.Detail.Should().Contain("were imported before it stopped").And.Contain("stay in the table");
    }

    [Fact]
    public async Task Rows_rejected_before_the_user_stopped_the_run_are_still_listed_in_the_details_file()
    {
        var h = ImportHarness.Create(Copy(), new HarnessOptions { Row = id => Clean(id, qty: id == 5 ? "abc" : null), CancelAtAdvance = 1 });

        await h.RunAsync();

        h.Completion!.Value.FeedbackPath.Should().NotBeNull();
        h.FeedbackCsv.Should().Contain("abc");
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Cancelled);
    }

    [Fact]
    public async Task Stopping_the_pre_check_of_an_abort_policy_import_leaves_the_table_untouched()
    {
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.AbortIfAnyIssue), new HarnessOptions { Row = id => Clean(id), CancelAtAdvance = 1 });

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Cancelled);
        h.Completion!.Value.Detail.Should().Contain("before anything was imported");
    }

    [Fact]
    public async Task Stopping_the_scan_of_an_exclude_groups_import_leaves_the_table_untouched()
    {
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.ExcludeDuplicateGroups), new HarnessOptions { Row = id => Clean(id), CancelAtAdvance = 1 });

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Cancelled);
    }

    [Fact]
    public async Task A_run_cancelled_while_it_waited_in_the_queue_never_starts_and_stays_cancelled()
    {
        var h = ImportHarness.Create(Copy(), new HarnessOptions { Row = id => Clean(id), CancelledWhileQueued = true });

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Advances.Should().Be(0);
        h.Completion.Should().BeNull("the cancel already ended it; the worker must not end it a second time");
        h.Notifications.Should().BeEmpty();
    }

    // ---- telling people ----

    [Fact]
    public async Task Every_way_a_run_can_end_sends_exactly_one_notification()
    {
        var finished = ImportHarness.Create(Copy(), new HarnessOptions { RowCount = 3, Row = id => Clean(id) });
        var cancelled = ImportHarness.Create(Copy(), new HarnessOptions { Row = id => Clean(id), CancelAtAdvance = 1 });
        var badConfig = ImportHarness.Create(new ImportDefinitionConfig { Name = "Broken", SourceTableId = Source, ImportType = ImportTypes.Copy, Mappings = [] });
        var interrupted = ImportHarness.Create(Copy(), new HarnessOptions { RowCount = 3, Row = id => Clean(id) });
        interrupted.Run.Status = ImportRunStatus.Running;
        var rejected = ImportHarness.Create(Copy(ImportConstraintPolicy.AbortIfAnyIssue), new HarnessOptions { RowCount = 3, Row = id => Clean(id, qty: "abc") });

        foreach (var h in new[] { finished, cancelled, badConfig, interrupted, rejected }) await h.RunAsync();

        foreach (var h in new[] { finished, cancelled, badConfig, interrupted, rejected }) h.Notifications.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_notification_carries_the_people_saved_with_the_run_and_the_link_address()
    {
        var config = Copy(notify: ["boss@example.com"]);
        var h = ImportHarness.Create(config, new HarnessOptions { RowCount = 2, Row = id => Clean(id) });
        h.Run.DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(ImportHarness.DestTableId, config, "https://app.example.com"));

        await h.RunAsync();

        var (_, snapshot) = h.Notifications.Single();
        snapshot.Config.NotifyEmails.Should().Equal("boss@example.com");
        snapshot.FrontendBaseUrl.Should().Be("https://app.example.com");
    }

    [Fact]
    public async Task A_notifier_that_throws_cannot_undo_or_fail_a_finished_run()
    {
        var h = ImportHarness.Create(Copy(), new HarnessOptions { RowCount = 3, Row = id => Clean(id) });
        var processor = h.Processor;
        h.Notifier.When(n => n.NotifyAsync(Arg.Any<ImportRun>(), Arg.Any<ImportRunSnapshot>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("mail server down"));

        var act = () => processor.RunAsync(h.Run.PublicId, CancellationToken.None);

        await act.Should().NotThrowAsync();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.Inserted.Should().HaveCount(3);
    }
}
