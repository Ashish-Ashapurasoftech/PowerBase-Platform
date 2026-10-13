using System.Text.Json;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Pipelines.Queries.GetCopyFailedRecords;
using PowerBase.Application.Pipelines.Queries.GetPipelineActivity;
using PowerBase.Application.Pipelines.Queries.ListPipelineRuns;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>The record that triggered each run (both run lists), and the paged read-back of a Copy Records step's rejected records.</summary>
public class PipelineRunTriggerRecordAndFailedRecordsHandlerTests
{
    // ── Run lists carry the triggering record ───────────────────────────────────────────────

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly Guid _appPublicId = Guid.NewGuid();

    private static PipelineRun Run(long id) => new() { Id = id, PublicId = Guid.NewGuid(), Status = "Success", TriggerType = "new-event", StartedOn = DateTime.UtcNow, AttemptCount = 1 };

    private List<(PipelineRun, string, Guid)> Runs(params PipelineRun[] runs)
    {
        _appRepo.GetIdByPublicIdAsync(_appPublicId, Arg.Any<CancellationToken>()).Returns(7L);
        _repo.CountRunsByAppIdAsync(7L, null, null, null, Arg.Any<CancellationToken>()).Returns(runs.Length);
        var rows = runs.Select(r => (r, "Flow", Guid.NewGuid())).ToList();
        _repo.GetRunsByAppIdAsync(7L, null, null, null, 1, 10, Arg.Any<CancellationToken>()).Returns(rows);
        return rows;
    }

    [Fact]
    public async Task ActivityList_CarriesEachRunsTriggerRecord_AndAsksForThemOncePerPage()
    {
        var withRecord = Run(1); var withoutRecord = Run(2);
        Runs(withRecord, withoutRecord);
        var recordPublicId = Guid.NewGuid();
        _repo.GetTriggerRecordRefsByRunIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, PipelineTriggerRecordRef> { [1] = new(recordPublicId, 4268) });
        var handler = new GetPipelineActivityQueryHandler(_repo, _appRepo, _userRepo);

        var result = await handler.HandleAsync(new GetPipelineActivityQuery(_appPublicId, null, null, null));

        Assert.Equal(4268, result.Items[0].TriggerRecordId);
        Assert.Equal(recordPublicId, result.Items[0].TriggerRecordPublicId);
        Assert.Null(result.Items[1].TriggerRecordId);          // a manual / scheduled run has no triggering record
        Assert.Null(result.Items[1].TriggerRecordPublicId);
        await _repo.Received(1).GetTriggerRecordRefsByRunIdsAsync(
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.OrderBy(i => i).SequenceEqual(new long[] { 1, 2 })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivityList_ARecordThatCanNoLongerBeFound_KeepsItsPublicIdWithoutANumericId()
    {
        Runs(Run(1));
        var recordPublicId = Guid.NewGuid();
        _repo.GetTriggerRecordRefsByRunIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, PipelineTriggerRecordRef> { [1] = new(recordPublicId, null) });

        var result = await new GetPipelineActivityQueryHandler(_repo, _appRepo, _userRepo).HandleAsync(new GetPipelineActivityQuery(_appPublicId, null, null, null));

        Assert.Null(result.Items[0].TriggerRecordId);
        Assert.Equal(recordPublicId, result.Items[0].TriggerRecordPublicId);
    }

    [Fact]
    public async Task AppRunList_CarriesEachRunsTriggerRecordToo()
    {
        Runs(Run(5));
        _repo.GetTriggerRecordRefsByRunIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, PipelineTriggerRecordRef> { [5] = new(Guid.NewGuid(), 99) });

        var result = await new ListAppPipelineRunsQueryHandler(_repo, _appRepo, _userRepo).HandleAsync(new ListAppPipelineRunsQuery(_appPublicId));

        Assert.Equal(99, result.Items[0].TriggerRecordId);
    }

    [Fact]
    public async Task RunLists_StillWork_WhenNoRunHasATriggerRecord()
    {
        Runs(Run(1));
        _repo.GetTriggerRecordRefsByRunIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, PipelineTriggerRecordRef>());

        var activity = await new GetPipelineActivityQueryHandler(_repo, _appRepo, _userRepo).HandleAsync(new GetPipelineActivityQuery(_appPublicId, null, null, null));
        var list = await new ListAppPipelineRunsQueryHandler(_repo, _appRepo, _userRepo).HandleAsync(new ListAppPipelineRunsQuery(_appPublicId));

        Assert.Null(activity.Items[0].TriggerRecordId);
        Assert.Null(list.Items[0].TriggerRecordId);
        Assert.Equal("Success", activity.Items[0].Status);   // everything else is as before
    }

    // ── Failed records read-back ────────────────────────────────────────────────────────────

    private static string Hex(string path) => Convert.ToHexString(CopyFailedRecordStore.Hash(path));

    private sealed class FailedRecordsFixture
    {
        public readonly IPipelineRepository Repo = Substitute.For<IPipelineRepository>();
        public readonly IPipelineStepIdempotencyRepository Receipts = Substitute.For<IPipelineStepIdempotencyRepository>();
        public readonly Dictionary<string, string> Store = new();
        public readonly Guid RunPublicId = Guid.NewGuid(), Message = Guid.NewGuid(), Step = Guid.NewGuid();
        public const long StepRunId = 77;
        public const string Path = "root/ref_1";

        public FailedRecordsFixture(int[]? pageSizes = null, bool storeSummary = true, Guid? snapshotStep = null)
        {
            Repo.GetRunByPublicIdAsync(RunPublicId, Arg.Any<CancellationToken>()).Returns(new PipelineRun { Id = 5, PipelineId = 9, MessageId = Message });
            Repo.GetStepRunsByRunIdAsync(5, Arg.Any<CancellationToken>()).Returns(new List<PipelineStepRun>
            {
                new() { Id = StepRunId, PipelineRunId = 5, StepId = 3, ExecutionPath = Path, StepPublicIdSnapshot = snapshotStep ?? Step }
            });
            Repo.GetStepsByPipelineIdAsync(9, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { new() { Id = 3, PublicId = Step } });
            Receipts.GetByExecutionKeyAsync(Message, Arg.Any<Guid>(), Arg.Any<byte[]>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>())
                .Returns(c => c.ArgAt<Guid>(1) == Step ? Store.GetValueOrDefault(Convert.ToHexString(c.Arg<byte[]>())) : null);
            pageSizes ??= new[] { 250, 250, 100 };
            var prefix = CopyFailedRecordStore.Prefix(Path);
            if (storeSummary)
                Store[Hex(prefix + "/complete")] = JsonSerializer.Serialize(new { InsertedCount = 1, ErrorCount = pageSizes.Sum(), FailedRecordPages = pageSizes.Length, FailedRecordsStored = pageSizes.Sum() });
            var n = 0;
            for (var page = 0; page < pageSizes.Length; page++)
                Store[Hex(prefix + CopyFailedRecordStore.PageSuffix(page))] = JsonSerializer.Serialize(
                    Enumerable.Range(0, pageSizes[page]).Select(_ => new CopyFailedRecord(Guid.NewGuid().ToString(), ++n, "k" + n, "reason " + n, new() { ["name"] = "v" + n })).ToList());
        }

        public GetCopyFailedRecordsQueryHandler Handler => new(Repo, Receipts);
    }

    [Fact]
    public async Task FailedRecords_ArePagedInTheOrderTheyWereKept()
    {
        var f = new FailedRecordsFixture();

        var first = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 1));
        var last = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 3));

        Assert.Equal(600, first.Total);
        Assert.Equal(250, first.Items.Count);
        Assert.Equal(1, first.Items[0].SourceRecordId);
        Assert.Equal("reason 1", first.Items[0].Reason);
        Assert.Equal("v1", first.Items[0].Values!["name"]);
        Assert.Equal(100, last.Items.Count);
        Assert.Equal(600, last.Items[^1].SourceRecordId);
        Assert.Equal(CopyFailedRecordStore.PageSize, first.PageSize);
    }

    [Fact]
    public async Task FailedRecords_EveryKeptRecordIsReachableByWalkingThePagesToTheEnd()
    {
        var f = new FailedRecordsFixture();
        var all = new List<CopyFailedRecord>();
        for (var page = 1; ; page++)
        {
            var result = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, page));
            if (result.Items.Count == 0) break;
            all.AddRange(result.Items);
        }

        Assert.Equal(600, all.Count);
        Assert.Equal(600, all.Select(r => r.SourcePublicId).Distinct().Count());
    }

    [Fact]
    public async Task FailedRecords_APagePastTheEnd_IsEmptyButStillReportsTheTotal()
    {
        var f = new FailedRecordsFixture();

        var result = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 4));

        Assert.Empty(result.Items);
        Assert.Equal(600, result.Total);
    }

    [Fact]
    public async Task FailedRecords_APageNumberBelowOne_IsTreatedAsTheFirstPage()
    {
        var f = new FailedRecordsFixture();

        var result = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 0));

        Assert.Equal(250, result.Items.Count);
        Assert.Equal(1, result.Page);
    }

    [Fact]
    public async Task FailedRecords_ARunBeforeTheyWereKept_IsEmptyNotAnError()
    {
        var f = new FailedRecordsFixture(storeSummary: false);   // no summary receipt at all (older run, or receipts already pruned)

        var result = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 1));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task FailedRecords_ASummaryWithoutTheNewFields_IsEmptyNotAnError()
    {
        var f = new FailedRecordsFixture(storeSummary: false);
        f.Store[Hex(CopyFailedRecordStore.Prefix(FailedRecordsFixture.Path) + "/complete")] = JsonSerializer.Serialize(new { InsertedCount = 3, ErrorCount = 17, Errors = new[] { "x" } });

        var result = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 1));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task FailedRecords_AStepRunWhoseSnapshotLacksThePublicId_FallsBackToTheStepsOwn()
    {
        var f = new FailedRecordsFixture(snapshotStep: Guid.Empty);

        var result = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 1));

        Assert.Equal(250, result.Items.Count);
    }

    [Fact]
    public async Task FailedRecords_ARunWithoutAMessageId_IsEmpty()
    {
        var f = new FailedRecordsFixture();
        f.Repo.GetRunByPublicIdAsync(f.RunPublicId, Arg.Any<CancellationToken>()).Returns(new PipelineRun { Id = 5, PipelineId = 9, MessageId = null });

        var result = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 1));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task FailedRecords_UnknownRun_IsNotFound()
    {
        var f = new FailedRecordsFixture();
        f.Repo.GetRunByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((PipelineRun?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(Guid.NewGuid(), FailedRecordsFixture.StepRunId, 1)));
    }

    [Fact]
    public async Task FailedRecords_AStepRunOfAnotherRun_IsNotFound()
    {
        var f = new FailedRecordsFixture();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, 12345, 1)));
    }

    [Fact]
    public async Task FailedRecords_ReceiptsOfAnotherStepAreNeverRead()
    {
        var f = new FailedRecordsFixture();
        f.Repo.GetStepRunsByRunIdAsync(5, Arg.Any<CancellationToken>()).Returns(new List<PipelineStepRun>
        {
            new() { Id = FailedRecordsFixture.StepRunId, PipelineRunId = 5, StepId = 3, ExecutionPath = FailedRecordsFixture.Path, StepPublicIdSnapshot = Guid.NewGuid() }   // a different step
        });

        var result = await f.Handler.HandleAsync(new GetCopyFailedRecordsQuery(f.RunPublicId, FailedRecordsFixture.StepRunId, 1));

        Assert.Empty(result.Items);   // the receipts are scoped by (run message, step): another step's pages are not exposed
    }
}
