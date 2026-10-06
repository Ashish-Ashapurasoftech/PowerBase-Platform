using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>A completion email that could not be sent is said on the run, not only in the log; a run that mailed fine says nothing.</summary>
public class ImportMailNoteTests
{
    private const string Note = "The completion email could not be sent: 5.7.1 Sender address not verified";

    private static async Task<ImportHarness> RunAsync(ImportDefinitionConfig? cfg, string? note, Action<HarnessOptions>? tweak = null)
    {
        var options = new HarnessOptions { RowCount = 5, ExtraDestinations = [new HarnessDestination
        {
            Id = 12, PublicId = ContactsId, Name = "Contacts",
            Fields = [ImportHarness.RecordId(12), ImportHarness.Field(12, 6, "Title", "Text")]
        }] };
        tweak?.Invoke(options);
        var h = ImportHarness.Create(cfg, options);
        h.Notifier.NotifyAsync(Arg.Any<ImportRun>(), Arg.Any<ImportRunSnapshot>(), Arg.Any<CancellationToken>()).Returns(note);
        await h.RunAsync();
        return h;
    }

    private static readonly Guid ContactsId = Guid.NewGuid();

    [Fact]
    public async Task A_single_table_run_whose_mail_failed_carries_the_reason_on_the_run()
    {
        var h = await RunAsync(null, Note);

        await h.Runs.Received(1).AppendDetailAsync(h.Run.Id, Note, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_multi_table_run_whose_mail_failed_carries_the_reason_on_the_run()
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Two", SourceTableId = ImportHarness.SourceTableId, Mappings = [new() { DestFid = 6, SourceFid = 6 }],
            AdditionalTargets = [new ImportTargetConfig { DestinationTableId = ContactsId, Mappings = [new() { DestFid = 6, SourceFid = 9 }] }]
        };

        var h = await RunAsync(cfg, Note);

        await h.Runs.Received(1).AppendDetailAsync(h.Run.Id, Note, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_run_whose_mail_went_out_adds_nothing()
    {
        var h = await RunAsync(null, null);

        await h.Runs.DidNotReceiveWithAnyArgs().AppendDetailAsync(default, default!, default);
    }

    [Fact]
    public async Task Failing_to_record_the_note_never_fails_the_run()
    {
        var h = ImportHarness.Create(null, new HarnessOptions { RowCount = 5 });
        h.Notifier.NotifyAsync(Arg.Any<ImportRun>(), Arg.Any<ImportRunSnapshot>(), Arg.Any<CancellationToken>()).Returns(Note);
        h.Runs.AppendDetailAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new InvalidOperationException("db"));

        var act = () => h.RunAsync();

        await act.Should().NotThrowAsync();
        h.Completion!.Value.Status.Should().NotBe(ImportRunStatus.Failed);
    }
}
