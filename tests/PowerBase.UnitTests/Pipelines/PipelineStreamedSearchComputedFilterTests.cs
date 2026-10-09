using System.Data;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Search Records → Loop over a table far too large to hold in memory, filtered on a Summary (or a Reference by its
/// display text). Such a condition cannot be matched in SQL, so the streamed search pages the rows that match the SQL
/// half, evaluates the rest on each page, stages only the matches and moves its checkpoint past every row it scanned.
/// </summary>
public class PipelineStreamedSearchComputedFilterTests
{
    private static readonly Guid TableId = Guid.NewGuid();

    private sealed class KeysetStub(params IReadOnlyList<IReadOnlyDictionary<string, object?>>[] pages) : IPipelineRecordSearchService, IKeysetPipelineRecordSearchService
    {
        public FilterGroup? SqlTreeSeen { get; private set; }
        public int PagesRead { get; private set; }
        public bool SupportsKeysetPaging => true;
        public Task<long> GetMaxRecordIdAsync(AppTable table, CancellationToken ct = default) => Task.FromResult(999L);
        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchPagesAsync(
            AppTable table, IReadOnlyList<AppField> fields, int pageSize, FilterGroup? filterTree = null,
            long afterId = 0, long maxId = long.MaxValue, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            SqlTreeSeen = filterTree;
            foreach (var page in pages) { PagesRead++; yield return page; await Task.Yield(); }
        }
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchAsync(AppTable table, IReadOnlyList<AppField> fields,
            int? maxResults = null, FilterGroup? filterTree = null, CancellationToken ct = default, int page = 1) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCopySnapshotAsync(
            AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
    }

    private static IReadOnlyDictionary<string, object?> Row(long id) =>
        new Dictionary<string, object?> { ["Id"] = id, ["PublicId"] = Guid.NewGuid(), ["f_6"] = $"r{id}" };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Page(params long[] ids) => ids.Select(Row).ToList();

    private sealed class Outcome
    {
        public List<PipelineBulkEventRecord> Staged { get; } = new();
        public List<long> Checkpoints { get; } = new();
        public KeysetStub Search { get; init; } = null!;
    }

    /// <summary>Runs the real engine; <paramref name="summaryById"/> is what each row's Summary is worth.</summary>
    private static async Task<Outcome> RunAsync(string field, string op, string value, IReadOnlyDictionary<long, object?> summaryById,
        params IReadOnlyList<IReadOnlyDictionary<string, object?>>[] pages)
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var relational = Substitute.For<IRelationalProjector>();
        var formula = Substitute.For<IFormulaProjector>();
        var search = new KeysetStub(pages);
        var outcome = new Outcome { Search = search };

        var messageId = Guid.NewGuid();
        var table = new AppTable { Id = 100, AppId = 1, PublicId = TableId };
        tableRepo.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(100, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 6, Fid = 6, Name = "Order no", TypeCode = "Text" },
            new() { Id = 14, Fid = 14, Name = "Sum of Amount", TypeCode = "Summary", Settings = "{\"function\":\"Sum\",\"targetTypeCode\":\"Number\"}" },
        });
        relational.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<long, object?>>>(
                ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(2)
                    .Select(r => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [14] = summaryById[Convert.ToInt64(r["Id"])] }).ToList()));
        formula.Project(Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(), Arg.Any<AppTable?>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyDictionary<long, object?>>?>(2)!);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(search);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        serviceProvider.GetService(typeof(IRelationalProjector)).Returns(relational);
        serviceProvider.GetService(typeof(IFormulaProjector)).Returns(formula);

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var idempotency = Substitute.For<IPipelineStepIdempotencyRepository>();
        var engine = new PipelineEngine(
            pipelineRepo, Substitute.For<IRecordRepository>(), Substitute.For<IRecordWriteService>(), tableRepo, fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), uow,
            Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), idempotency);
        typeof(PipelineEngine).GetField("_pipelineRecordSearchService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(engine, search);

        var rules = JsonSerializer.Serialize(new[] { new { Field = field, Operator = op, Value = value } });
        pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true });
        // A long run keeps its lease: the heartbeat (every 15 s) must be told the lease is still ours.
        pipelineRepo.ExtendRunLeaseAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new() { Id = 11, RefId = "search", Type = "query", Subtype = "search-records",
                ConfigJson = $"{{\"TableId\":\"{TableId}\",\"FilterGroups\":[{{\"LogicalOp\":\"AND\",\"Rules\":{rules}}}]}}" },
            new() { Id = 12, RefId = "loop", Type = "loop", Subtype = "for-each", ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "search" }) }
        });
        pipelineRepo.GetOrCreateSearchWorksetAsync(Arg.Any<Guid>(), messageId, "search", 999, Arg.Any<CancellationToken>())
            .Returns(call => new PipelineSearchWorkset { WorksetId = call.ArgAt<Guid>(0), RunMessageId = messageId, StepRefId = "search", SnapshotMaxRecordId = 999 });
        var nextId = 0L;
        pipelineRepo.When(repo => repo.AppendSearchWorksetPageAsync(Arg.Any<PipelineSearchWorkset>(), Arg.Any<List<PipelineBulkEventRecord>>(), Arg.Any<long>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                foreach (var row in call.ArgAt<List<PipelineBulkEventRecord>>(1)) { row.Id = ++nextId; outcome.Staged.Add(row); }
                outcome.Checkpoints.Add(call.ArgAt<long>(2));
            });
        pipelineRepo.GetPendingSearchWorksetPageAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => outcome.Staged.Where(row => row.Processed == 0).Take(500).ToList());
        pipelineRepo.When(repo => repo.MarkSearchWorksetRecordsProcessedAsync(Arg.Any<Guid>(), Arg.Any<List<long>>(), 1, Arg.Any<CancellationToken>()))
            .Do(call => { foreach (var id in call.ArgAt<List<long>>(1)) outcome.Staged.Single(row => row.Id == id).Processed = 1; });

        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "manual", TriggerPayloadJson = "{}", MessageId = messageId.ToString() };
        await engine.ExecuteAsync(task, CancellationToken.None);
        return outcome;
    }

    private static Dictionary<long, object?> Sums(params (long Id, decimal Sum)[] values) =>
        values.ToDictionary(v => v.Id, v => (object?)v.Sum);

    [Fact]
    public async Task SummaryFilter_OnAStreamedSearch_StagesOnlyTheMatchingRows_AndNeverSendsTheConditionToSql()
    {
        var outcome = await RunAsync("fid_14", "greater_than", "100",
            Sums((1, 50), (2, 1800), (3, 100), (4, 101), (5, 5)),
            Page(1, 2, 3), Page(4, 5));

        Assert.Equal(2, outcome.Staged.Count);
        Assert.Equal(new[] { 2L, 4L }, outcome.Staged.Select(r => JsonDocument.Parse(r.AfterValuesJson!).RootElement.GetProperty("Id").GetInt64()).ToArray());
        Assert.Null(outcome.Search.SqlTreeSeen?.Nodes.FirstOrDefault(n => n.Condition?.FieldId == 14));
    }

    [Fact]
    public async Task SummaryFilter_TheStagedRowsCarryTheComputedValue()
    {
        var outcome = await RunAsync("fid_14", "greater_than", "100", Sums((1, 1800)), Page(1));

        var staged = JsonDocument.Parse(Assert.Single(outcome.Staged).AfterValuesJson!).RootElement;
        Assert.Equal(1800m, staged.GetProperty("fid_14").GetDecimal());
    }

    [Fact]
    public async Task SummaryFilter_CheckpointAdvancesPastRowsTheConditionRejected_IncludingAWholePageOfThem()
    {
        var outcome = await RunAsync("fid_14", "greater_than", "100",
            Sums((1, 1), (2, 2), (3, 3), (4, 4), (5, 500)),
            Page(1, 2), Page(3, 4), Page(5));

        // Pages 1 and 2 matched nothing and still moved the checkpoint, so a restarted step resumes after them.
        Assert.Equal(new long[] { 2, 4, 5 }, outcome.Checkpoints);
        Assert.Single(outcome.Staged);
        Assert.Equal(3, outcome.Search.PagesRead);
    }

    [Fact]
    public async Task SummaryFilter_NothingMatches_StillCompletesWithAnEmptyResult()
    {
        var outcome = await RunAsync("fid_14", "greater_than", "100000", Sums((1, 1), (2, 2)), Page(1, 2));

        Assert.Empty(outcome.Staged);
        Assert.Equal(new long[] { 2 }, outcome.Checkpoints);
    }

    [Fact]
    public async Task StoredFieldFilter_StreamsAsBefore_TheConditionGoesToSql()
    {
        var outcome = await RunAsync("fid_6", "is", "r2", Sums((1, 1), (2, 2)), Page(1, 2));

        Assert.NotNull(outcome.Search.SqlTreeSeen?.Nodes.FirstOrDefault(n => n.Condition?.FieldId == 6));
        Assert.Equal(2, outcome.Staged.Count);      // the SQL half is the stub's job; no in-memory pass removes rows
    }

    [Fact]
    public async Task ManyThousandsOfRows_AreScannedToTheEnd_NothingStopsOrIsDropped()
    {
        // 40 pages x 500 rows = 20,000 scanned (twice the old 10,000 ceiling); every third row matches.
        const int pageCount = 40, pageSize = 500, total = pageCount * pageSize;
        var sums = Enumerable.Range(1, total).ToDictionary(id => (long)id, id => (object?)(id % 3 == 0 ? 500m : 1m));
        var pages = Enumerable.Range(0, pageCount)
            .Select(p => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)Enumerable.Range(1, pageSize).Select(i => Row(p * pageSize + i)).ToList())
            .ToArray();

        var outcome = await RunAsync("fid_14", "greater_than", "100", sums, pages);

        Assert.Equal(pageCount, outcome.Search.PagesRead);
        Assert.Equal(total / 3, outcome.Staged.Count);
        Assert.Equal(pageCount, outcome.Checkpoints.Count);
        Assert.Equal(total, outcome.Checkpoints[^1]);                                    // resumed from here after a restart
        Assert.True(outcome.Checkpoints.Zip(outcome.Checkpoints.Skip(1), (a, b) => b > a).All(x => x), "checkpoints only move forward");
        Assert.Equal(total / 3, outcome.Staged.Select(r => r.Ordinal).Distinct().Count());   // every staged record has its own ordinal
        Assert.All(outcome.Staged, r => Assert.Equal(1, r.Processed));                       // and the loop processed all of them
    }
}
