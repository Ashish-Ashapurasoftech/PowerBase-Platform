using System.Data;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Search Records → Loop over a table too large to hold in memory (the streamed search). Where Azure AI Search can answer the
/// filter exactly it proposes candidate record ids, page by page; each page is read from SQL with the complete filter, staged,
/// and the AI cursor is saved with it. SQL answers everything else, a search the index finds nothing for, and one it fails
/// on before anything was staged. A workset keeps the source it started with.
/// </summary>
public class PipelineStreamedAiSearchTests
{
    private static readonly Guid TableId = Guid.NewGuid();

    // ── fakes ────────────────────────────────────────────────────────────────────────────────

    private sealed record DataRow(long Id, Guid PublicId, string Status);

    /// <summary>A SQL table: applies the id restriction, the snapshot limit and an "equals" on field 6 like the database would.</summary>
    private sealed class SqlStub(List<DataRow> data, long maxRecordId = 999) : IPipelineRecordSearchService, IKeysetPipelineRecordSearchService
    {
        public List<FilterGroup?> TreesSeen { get; } = new();
        public bool SupportsKeysetPaging => true;
        public Task<long> GetMaxRecordIdAsync(AppTable table, CancellationToken ct = default) => Task.FromResult(maxRecordId);

        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchPagesAsync(
            AppTable table, IReadOnlyList<AppField> fields, int pageSize, FilterGroup? filterTree = null,
            long afterId = 0, long maxId = long.MaxValue, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            TreesSeen.Add(filterTree);
            var rows = data.Where(r => r.Id > afterId && r.Id <= maxId && Matches(filterTree, r)).OrderBy(r => r.Id).ToList();
            foreach (var page in rows.Chunk(pageSize))
            {
                yield return page.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    { ["Id"] = r.Id, ["PublicId"] = r.PublicId, ["f_6"] = r.Status }).ToList();
                await Task.Yield();
            }
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchAsync(AppTable table, IReadOnlyList<AppField> fields,
            int? maxResults = null, FilterGroup? filterTree = null, CancellationToken ct = default, int page = 1) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);

        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCopySnapshotAsync(
            AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }

        private static bool Matches(FilterGroup? group, DataRow row)
        {
            if (group == null || group.Nodes.Count == 0) return true;
            bool Node(FilterNode n) => n.Condition != null ? Cond(n.Condition, row) : n.Group == null || Matches(n.Group, row);
            return string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase) ? group.Nodes.Any(Node) : group.Nodes.All(Node);
        }

        private static bool Cond(FilterCondition c, DataRow row) => (c.FieldId, c.Operator) switch
        {
            (3, "in") => JsonSerializer.Deserialize<long[]>(c.Value!)!.Contains(row.Id),
            (3, "lte") => row.Id <= long.Parse(c.Value!),
            (6, "eq") => string.Equals(row.Status, c.Value, StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    /// <summary>An index that proposes fixed pages of ids and honours the resume cursor.</summary>
    private sealed class AiFake
    {
        public IAzureSearchService Service { get; } = Substitute.For<IAzureSearchService>();
        public List<string?> CursorsAskedFor { get; } = new();
        public int PagesServed { get; private set; }

        public AiFake(params (string Cursor, Guid[] Ids)[] pages)
        {
            Service.IsGridSearchEnabled.Returns(true);
            Service.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
            Service.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(ci => Serve(ci.ArgAt<string?>(3), pages));
        }

        private async IAsyncEnumerable<AiSearchIdPage> Serve(string? after, (string Cursor, Guid[] Ids)[] pages)
        {
            CursorsAskedFor.Add(after);
            var skip = after == null ? 0 : Array.FindIndex(pages, p => p.Cursor == after) + 1;
            foreach (var (cursor, ids) in pages.Skip(skip)) { PagesServed++; yield return new AiSearchIdPage(ids, cursor); await Task.Yield(); }
        }

        /// <summary>How many candidates the index reports for the filter (what a MaxResults search asks before it reads any).</summary>
        public AiFake WithTotal(long total)
        {
            Service.SearchRecordsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), 1, 1, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(((IReadOnlyList<Guid>)new List<Guid>(), total)));
            return this;
        }

        public AiFake Throwing(Func<Exception> error, int afterPages)
        {
            Service.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(ci => ServeThenThrow(ci.ArgAt<string?>(3), afterPages, error));
            return this;
        }

        private async IAsyncEnumerable<AiSearchIdPage> ServeThenThrow(string? after, int afterPages, Func<Exception> error)
        {
            CursorsAskedFor.Add(after);
            await Task.Yield();
            for (var i = 0; i < afterPages; i++) { PagesServed++; yield return new AiSearchIdPage([Guid.Empty], "x"); }
            throw error();
        }
    }

    private sealed class Outcome
    {
        public List<PipelineBulkEventRecord> Staged { get; } = new();
        public List<string> AiCursors { get; } = new();
        public List<long> SqlCheckpoints { get; } = new();
        public List<long> LagReadsAfterIds { get; } = new();
        public int BeginAiCalls, UseSqlCalls, CompleteCalls;
        public SqlStub Sql { get; init; } = null!;
        public PipelineSearchWorkset Workset { get; init; } = null!;
        public Exception? Error { get; set; }
        public List<long> StagedIds => Staged.Select(r => JsonDocument.Parse(r.AfterValuesJson!).RootElement.GetProperty("Id").GetInt64()).ToList();
    }

    private static DataRow Data(long id, string status = "Active") => new(id, Guid.NewGuid(), status);

    private static (string Cursor, Guid[] Ids) AiPage(string cursor, params DataRow[] rows) => (cursor, rows.Select(r => r.PublicId).ToArray());

    /// <param name="recent">The records changed within the index-lag window (id, when they changed), as the database would list them.</param>
    /// <param name="recentWhenPageRead">Which records count as recently changed when an index page is read; defaults to <paramref name="recent"/>.
    /// A record missing here but in <paramref name="recent"/> changed after its page was read.</param>
    private static async Task<Outcome> RunAsync(
        List<DataRow> data, AiFake? ai, PipelineSearchWorkset? workset = null, bool searchable = true, bool filterable = true,
        bool encrypted = false, int? maxResults = null, int? streamAbove = null, string op = "is", string value = "Active", long snapshotMax = 999,
        int? lagSeconds = null, IReadOnlyList<(long Id, DateTime ChangedOn)>? recent = null, IReadOnlyCollection<long>? recentWhenPageRead = null, bool patternOperators = false)
    {
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var sql = new SqlStub(data, snapshotMax);
        var messageId = Guid.NewGuid();
        workset ??= new PipelineSearchWorkset { StepRefId = "search", SnapshotMaxRecordId = snapshotMax, RunMessageId = messageId };
        var outcome = new Outcome { Sql = sql, Workset = workset };

        var table = new AppTable { Id = 100, AppId = 1, PublicId = TableId };
        tableRepo.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(100, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 6, Fid = 6, Name = "Order no", TypeCode = "Text", IsSearchable = searchable, IsFilterable = filterable, IsEncrypted = encrypted, SearchSlot = "str_01" }
        });
        recordRepo.GetIdsByPublicIdsAsync(table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<long>>(
                data.Where(r => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Contains(r.PublicId)).Select(r => r.Id).ToList()));
        recent ??= [];
        var recentAtPageRead = (recentWhenPageRead ?? recent.Select(r => r.Id).ToList()).ToHashSet();
        recordRepo.ListRecordsChangedSinceAsync(table, Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                outcome.LagReadsAfterIds.Add(ci.ArgAt<long>(2));
                return Task.FromResult<IReadOnlyList<(long Id, DateTime ChangedOn)>>(recent
                    .Where(r => r.Id > ci.ArgAt<long>(2) && r.Id <= ci.ArgAt<long>(3)).OrderBy(r => r.Id).Take(ci.ArgAt<int>(4)).ToList());
            });
        recordRepo.GetRecordIdsChangedSinceAsync(table, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlySet<long>>(ci.ArgAt<IReadOnlyCollection<long>>(1).Where(recentAtPageRead.Contains).ToHashSet()));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(sql);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        if (ai != null) serviceProvider.GetService(typeof(IAzureSearchService)).Returns(ai.Service);

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var options = new PipelineExecutionOptions { AiSearchMinTableRows = 0, AiSearchCountGuard = false, AiSearchFieldSettleHours = 0, AiSearchLimitedReadMaxMatches = 1000, AiSearchPatternOperators = patternOperators };
        if (streamAbove.HasValue) options.StreamSearchAboveRecords = streamAbove.Value;
        if (lagSeconds.HasValue) options.AiSearchIndexLagSeconds = lagSeconds.Value;
        var engine = new PipelineEngine(
            pipelineRepo, recordRepo, Substitute.For<IRecordWriteService>(), tableRepo, fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(options),
            Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(), uow,
            Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>());
        typeof(PipelineEngine).GetField("_pipelineRecordSearchService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(engine, sql);

        var rules = JsonSerializer.Serialize(new[] { new { Field = "fid_6", Operator = op, Value = value } });
        var maxResultsJson = maxResults.HasValue ? $",\"MaxResults\":{maxResults}" : "";
        pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true });
        pipelineRepo.ExtendRunLeaseAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new() { Id = 11, RefId = "search", Type = "query", Subtype = "search-records",
                ConfigJson = $"{{\"TableId\":\"{TableId}\"{maxResultsJson},\"FilterGroups\":[{{\"LogicalOp\":\"AND\",\"Rules\":{rules}}}]}}" },
            new() { Id = 12, RefId = "loop", Type = "loop", Subtype = "for-each", ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "search" }) }
        });
        pipelineRepo.GetOrCreateSearchWorksetAsync(Arg.Any<Guid>(), messageId, "search", snapshotMax, Arg.Any<CancellationToken>())
            .Returns(call => { workset.WorksetId = call.ArgAt<Guid>(0); return workset; });

        var nextId = 0L;
        string savedCursor = workset.LastSearchCursor ?? "";
        pipelineRepo.BeginAiSearchWorksetDiscoveryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { outcome.BeginAiCalls++; return Task.CompletedTask; });
        pipelineRepo.UseSqlForSearchWorksetDiscoveryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { outcome.UseSqlCalls++; savedCursor = ""; return Task.CompletedTask; });
        pipelineRepo.AppendSearchWorksetAiPageAsync(Arg.Any<PipelineSearchWorkset>(), Arg.Any<List<PipelineBulkEventRecord>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // What the repository's UPDATE enforces: the workset must still be at the cursor we last saved.
                if ((call.ArgAt<PipelineSearchWorkset>(0).LastSearchCursor ?? "") != savedCursor)
                    throw new InvalidOperationException("AI cursor changed concurrently.");
                savedCursor = call.ArgAt<string>(2);
                foreach (var row in call.ArgAt<List<PipelineBulkEventRecord>>(1)) { row.Id = ++nextId; outcome.Staged.Add(row); }
                outcome.AiCursors.Add(call.ArgAt<string>(2));
                return Task.CompletedTask;
            });
        pipelineRepo.AppendSearchWorksetPageAsync(Arg.Any<PipelineSearchWorkset>(), Arg.Any<List<PipelineBulkEventRecord>>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach (var row in call.ArgAt<List<PipelineBulkEventRecord>>(1)) { row.Id = ++nextId; outcome.Staged.Add(row); }
                outcome.SqlCheckpoints.Add(call.ArgAt<long>(2));
                return Task.CompletedTask;
            });
        pipelineRepo.GetStagedSearchWorksetRecordsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlySet<Guid>>(outcome.Staged.Select(r => r.RecordPublicId)
                .Where(ci.ArgAt<IReadOnlyCollection<Guid>>(1).Contains).ToHashSet()));
        pipelineRepo.CompleteSearchWorksetDiscoveryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { outcome.CompleteCalls++; return Task.CompletedTask; });
        pipelineRepo.GetPendingSearchWorksetPageAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<long>())
            .Returns(_ => outcome.Staged.Where(row => row.Processed == 0).Take(500).ToList());
        pipelineRepo.When(repo => repo.MarkSearchWorksetRecordsProcessedAsync(Arg.Any<Guid>(), Arg.Any<List<long>>(), 1, Arg.Any<CancellationToken>()))
            .Do(call => { foreach (var id in call.ArgAt<List<long>>(1)) outcome.Staged.Single(row => row.Id == id).Processed = 1; });

        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "manual", TriggerPayloadJson = "{}", MessageId = messageId.ToString() };
        try { await engine.ExecuteAsync(task, CancellationToken.None); }
        catch (Exception ex) { outcome.Error = ex; }
        return outcome;
    }

    private static bool UsedTheOriginalSqlFilter(Outcome o) =>
        o.Sql.TreesSeen.Any(t => t != null && !t.Nodes.Any(n => n.Condition is { FieldId: 3, Operator: "in" }));

    // ── the index answers ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EligibleSearch_StagesTheVerifiedCandidatesPageByPage_SavingTheCursorWithEachPage()
    {
        var data = Enumerable.Range(1, 5).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("b", data[0], data[1], data[2]), AiPage("~", data[3], data[4]));

        var o = await RunAsync(data, ai);

        Assert.Null(o.Error);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, o.StagedIds);
        Assert.Equal(new[] { "b", "~", PipelineEngine.AiIndexLagEndCursor }, o.AiCursors);   // then the (empty) index-lag tail
        Assert.Equal(1, o.BeginAiCalls);
        Assert.Equal(0, o.UseSqlCalls);
        Assert.Equal(1, o.CompleteCalls);
        Assert.False(UsedTheOriginalSqlFilter(o), "the table must not be scanned when the index answers");
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, o.Staged.Select(r => r.Ordinal));
        Assert.Equal("Ai", o.Workset.DiscoverySource);
    }

    [Fact]
    public async Task EveryCandidateIsReadFromSqlWithTheOriginalConditionStillApplied()
    {
        var data = new List<DataRow> { Data(1), Data(2, "Closed"), Data(3) };
        var ai = new AiFake(AiPage("~", data.ToArray()));   // the index proposes all three; one has since changed to Closed

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 1, 3 }, o.StagedIds);
        Assert.All(o.Sql.TreesSeen, t => Assert.Contains(t!.Nodes, n => n.Condition is { FieldId: 6, Operator: "eq" }));
    }

    [Fact]
    public async Task CandidateThatNoLongerExists_IsNotStaged_AndTheCursorStillAdvances()
    {
        var data = new List<DataRow> { Data(1), Data(2) };
        var deleted = Data(3);                              // proposed by the index, gone from SQL
        var ai = new AiFake(AiPage("a", data[0], deleted), AiPage("~", data[1]));

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 1, 2 }, o.StagedIds);
        Assert.Equal(new[] { "a", "~", PipelineEngine.AiIndexLagEndCursor }, o.AiCursors);
    }

    [Fact]
    public async Task PageWhoseCandidatesAllFailVerification_AdvancesTheCursorAndStagesNothing()
    {
        var data = new List<DataRow> { Data(1, "Closed"), Data(2) };
        var ai = new AiFake(AiPage("a", data[0]), AiPage("~", data[1]));

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 2 }, o.StagedIds);
        Assert.Equal(new[] { "a", "~", PipelineEngine.AiIndexLagEndCursor }, o.AiCursors);
    }

    [Fact]
    public async Task RecordsCreatedAfterTheSearchStarted_AreNotStaged()
    {
        // The snapshot (max record id 999 when the step started) keeps a loop from picking up records it creates itself.
        var data = new List<DataRow> { Data(1), Data(1000) };
        var ai = new AiFake(AiPage("~", data.ToArray()));

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 1 }, o.StagedIds);
    }

    [Fact]
    public async Task StagedItemsAreInRecordIdOrderWithinAPage()
    {
        var data = new List<DataRow> { Data(1), Data(2), Data(3) };
        var ai = new AiFake(AiPage("~", data[2], data[0], data[1]));   // the index returns them in id-text order

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 1, 2, 3 }, o.StagedIds);
    }

    [Fact]
    public async Task ManyPagesOfMatches_AreAllStaged_NothingIsCut()
    {
        // Every page the index serves is staged: there is no ceiling in the streamed path (the 50,000-id read itself is covered
        // by AiIdRangePagerTests, which reads 120,000). Kept to six pages because the loop that consumes the staged rows runs
        // them one by one.
        const int pageCount = 6, perPage = 1000;
        var data = Enumerable.Range(1, pageCount * perPage).Select(i => Data(i)).ToList();
        var pages = Enumerable.Range(0, pageCount).Select(p => AiPage($"c{p:D3}", data.Skip(p * perPage).Take(perPage).ToArray())).ToArray();
        var ai = new AiFake(pages);

        var o = await RunAsync(data, ai, snapshotMax: pageCount * perPage);

        Assert.Null(o.Error);
        Assert.Equal(pageCount * perPage, o.Staged.Count);
        Assert.Equal(pageCount * perPage, o.StagedIds.Distinct().Count());
        Assert.Equal(pageCount + 1, o.AiCursors.Count);   // and the index-lag tail
    }

    [Fact]
    public async Task EncryptedField_StillHasItsConditionCheckedInMemoryAgainstTheDecryptedValue()
    {
        // The index (plaintext) proposes both; the SQL read cannot compare ciphertext, so the engine's in-memory check decides.
        var data = new List<DataRow> { Data(1), Data(2, "Other") };
        var ai = new AiFake(AiPage("~", data.ToArray()));

        var o = await RunAsync(data, ai, encrypted: true);

        Assert.Equal(new long[] { 1 }, o.StagedIds);
        Assert.All(o.Sql.TreesSeen, t => Assert.DoesNotContain(t!.Nodes, n => n.Condition is { FieldId: 6 }));   // never sent to SQL
    }

    [Fact]
    public async Task MaxResults_WithFewEnoughCandidates_ReadsThemAllAndStagesTheFirstByRecordId_AsSqlWould()
    {
        // The index lists the HIGH ids first. "The first 3" must still be records 1, 2, 3, as the SQL search returns them.
        var data = Enumerable.Range(1, 6).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("a", data[5], data[4]), AiPage("b", data[3], data[2]), AiPage("~", data[1], data[0])).WithTotal(6);

        var o = await RunAsync(data, ai, maxResults: 3, streamAbove: 2);

        Assert.Null(o.Error);
        Assert.Equal(new long[] { 1, 2, 3 }, o.StagedIds);
        Assert.Equal(3, ai.PagesServed);                                      // every candidate was read
        Assert.Equal(new[] { PipelineEngine.AiIndexLagEndCursor }, o.AiCursors);   // staged in one go, index and lag tail done
        Assert.Equal(0, o.UseSqlCalls);
    }

    [Fact]
    public async Task MaxResults_AMatchOnlyTheLagTailKnows_CompetesForTheFirstNByRecordId()
    {
        // Record 1 changed a moment ago and is not (yet) proposed by the index; it has the lowest id, so it is among the first 3.
        var data = Enumerable.Range(1, 6).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data[5], data[4], data[3])).WithTotal(3);

        var o = await RunAsync(data, ai, maxResults: 3, streamAbove: 2, recent: [(1, BeforeTheSearch)]);

        Assert.Null(o.Error);
        Assert.Equal(new long[] { 1, 4, 5 }, o.StagedIds);
    }

    [Fact]
    public async Task MaxResults_WithMoreCandidatesThanTheLimitedReadAllows_IsReadFromSql()
    {
        var data = Enumerable.Range(1, 6).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data.ToArray())).WithTotal(1001);   // above AiSearchLimitedReadMaxMatches

        var o = await RunAsync(data, ai, maxResults: 3, streamAbove: 2);

        Assert.Empty(ai.CursorsAskedFor);                    // the index was only asked how many
        Assert.Equal(new long[] { 1, 2, 3 }, o.StagedIds);   // SQL stops at the limit, in record id order
        Assert.Equal(1, o.UseSqlCalls);
    }

    // ── index lag ────────────────────────────────────────────────────────────────────────────

    private static DateTime BeforeTheSearch => DateTime.UtcNow.AddMinutes(-1);
    private static DateTime DuringTheSearch => DateTime.UtcNow.AddMinutes(1);

    [Fact]
    public async Task RecordTheIndexHasNotCaughtUpWith_IsStagedFromTheIndexLagTail()
    {
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data[0], data[1]));     // record 3 was created a moment ago: not in the index yet

        var o = await RunAsync(data, ai, recent: [(3, BeforeTheSearch)]);

        Assert.Null(o.Error);
        Assert.Equal(new long[] { 1, 2, 3 }, o.StagedIds);
        Assert.Equal(new[] { "~", PipelineEngine.AiIndexLagEndCursor }, o.AiCursors);
        Assert.Equal(new[] { 1, 2, 3 }, o.Staged.Select(r => r.Ordinal));
        Assert.Equal(0, o.UseSqlCalls);
    }

    [Fact]
    public async Task RecordChangedToMatch_ButStillIndexedWithItsOldValue_IsStagedFromTheIndexLagTail()
    {
        // Record 2 was Closed when indexed and was set Active a moment ago: the index does not propose it.
        var data = new List<DataRow> { Data(1), Data(2) };
        var ai = new AiFake(AiPage("~", data[0]));

        var o = await RunAsync(data, ai, recent: [(2, BeforeTheSearch)]);

        Assert.Equal(new long[] { 1, 2 }, o.StagedIds);
    }

    [Fact]
    public async Task RecentRecordTheIndexAlsoFound_IsStagedOnce()
    {
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data.ToArray()));

        var o = await RunAsync(data, ai, recent: [(2, BeforeTheSearch), (3, BeforeTheSearch)]);

        Assert.Equal(new long[] { 1, 2, 3 }, o.StagedIds);   // 2 and 3 come from the tail, never from the index page too
        Assert.Equal(3, o.Staged.Select(r => r.RecordPublicId).Distinct().Count());
    }

    [Fact]
    public async Task RecentRecordThatDoesNotMatch_IsNotStaged()
    {
        var data = new List<DataRow> { Data(1), Data(2, "Closed") };
        var ai = new AiFake(AiPage("~", data[0]));

        var o = await RunAsync(data, ai, recent: [(2, BeforeTheSearch)]);

        Assert.Equal(new long[] { 1 }, o.StagedIds);         // the tail is read with the complete filter
    }

    [Fact]
    public async Task RecordChangedAfterItsIndexPageWasStaged_IsNotStagedAgainFromTheTail()
    {
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data.ToArray()));

        // Record 2 was not recent when its page was read, then changed while the search ran.
        var o = await RunAsync(data, ai, recent: [(2, DuringTheSearch)], recentWhenPageRead: []);

        Assert.Equal(new long[] { 1, 2, 3 }, o.StagedIds);
    }

    [Fact]
    public async Task RecentRecordCreatedAfterTheSearchStarted_IsNotStaged()
    {
        var data = new List<DataRow> { Data(1), Data(1000) };   // above the snapshot (999)
        var ai = new AiFake(AiPage("~", data[0]));

        var o = await RunAsync(data, ai, recent: [(1000, DuringTheSearch)]);

        Assert.Equal(new long[] { 1 }, o.StagedIds);
    }

    [Fact]
    public async Task LongIndexLagTail_IsStagedPageByPage_SavingItsCursor()
    {
        var data = Enumerable.Range(1, PipelineEngine.AiCandidateRecordIdChunkSize + 2).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data[0]));
        var recent = data.Skip(1).Select(r => (r.Id, BeforeTheSearch)).ToList();

        var o = await RunAsync(data, ai, recent: recent, snapshotMax: data.Count);

        Assert.Null(o.Error);
        Assert.Equal(data.Select(r => r.Id), o.StagedIds);
        var lastOfFirstTailPage = PipelineEngine.AiCandidateRecordIdChunkSize + 1;
        Assert.Equal(new[] { "~", PipelineEngine.AiIndexLagCursorPrefix + lastOfFirstTailPage, PipelineEngine.AiIndexLagEndCursor }, o.AiCursors);
    }

    [Fact]
    public async Task ResumedWorkset_ContinuesItsIndexLagTailFromItsCursor_WithoutTheIndex()
    {
        var data = Enumerable.Range(1, 5).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data[0], data[1]));
        ai.Service.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(false);          // the index may even be down now
        var workset = new PipelineSearchWorkset
        {
            DiscoverySource = "Ai", LastSearchCursor = PipelineEngine.AiIndexLagCursorPrefix + "3", DiscoveredCount = 3, SnapshotMaxRecordId = 999
        };

        var o = await RunAsync(data, ai, workset, recent: [(3, BeforeTheSearch), (4, BeforeTheSearch), (5, BeforeTheSearch)]);

        Assert.Null(o.Error);
        Assert.Empty(ai.CursorsAskedFor);
        Assert.Equal(new long[] { 3 }, o.LagReadsAfterIds);   // carried on after record 3
        Assert.Equal(new long[] { 4, 5 }, o.StagedIds);
        Assert.Equal(new[] { 4, 5 }, o.Staged.Select(r => r.Ordinal));
        Assert.Equal(1, o.CompleteCalls);
    }

    [Fact]
    public async Task IndexLagTailAlreadyReadToTheEnd_IsCompletedOnRetry()
    {
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data.ToArray()));
        var workset = new PipelineSearchWorkset
        {
            DiscoverySource = "Ai", LastSearchCursor = PipelineEngine.AiIndexLagEndCursor, DiscoveredCount = 3, SnapshotMaxRecordId = 999
        };

        var o = await RunAsync(data, ai, workset, recent: [(3, BeforeTheSearch)]);

        Assert.Null(o.Error);
        Assert.Empty(ai.CursorsAskedFor);
        Assert.Empty(o.LagReadsAfterIds);
        Assert.Empty(o.Staged);
        Assert.Equal(1, o.CompleteCalls);
    }

    [Fact]
    public async Task IndexLagTurnedOff_OnlyTheIndexIsRead()
    {
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data[0], data[1]));

        var o = await RunAsync(data, ai, lagSeconds: 0, recent: [(3, BeforeTheSearch)]);

        Assert.Equal(new long[] { 1, 2 }, o.StagedIds);
        Assert.Equal(new[] { "~" }, o.AiCursors);
        Assert.Empty(o.LagReadsAfterIds);
    }

    // ── resume ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResumedWorkset_ContinuesFromItsAiCursorAndOrdinal()
    {
        var data = Enumerable.Range(1, 6).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("a", data[0], data[1]), AiPage("b", data[2], data[3]), AiPage("~", data[4], data[5]));
        var workset = new PipelineSearchWorkset { DiscoverySource = "Ai", LastSearchCursor = "a", DiscoveredCount = 2, SnapshotMaxRecordId = 999 };

        var o = await RunAsync(data, ai, workset);

        Assert.Equal(new[] { "a" }, ai.CursorsAskedFor);
        Assert.Equal(new long[] { 3, 4, 5, 6 }, o.StagedIds);
        Assert.Equal(new[] { 3, 4, 5, 6 }, o.Staged.Select(r => r.Ordinal));      // numbering carries on after the 2 already staged
        Assert.Equal(0, o.BeginAiCalls);                                           // already an AI workset
    }

    [Fact]
    public async Task WorksetAlreadyFilledFromSql_StaysOnSql_TheIndexIsNeverAsked()
    {
        var data = Enumerable.Range(1, 5).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data.ToArray()));
        var workset = new PipelineSearchWorkset { DiscoverySource = "Sql", LastRecordId = 2, DiscoveredCount = 2, SnapshotMaxRecordId = 999 };

        var o = await RunAsync(data, ai, workset);

        Assert.Empty(ai.CursorsAskedFor);
        await ai.Service.DidNotReceive().IsHealthyAsync(Arg.Any<CancellationToken>());
        Assert.Equal(new long[] { 3, 4, 5 }, o.StagedIds);                          // continued from LastRecordId
    }

    [Fact]
    public async Task AiReadThatAlreadyReachedTheEnd_IsCompletedOnRetryWithoutTheIndex()
    {
        // The step was interrupted after the last page was staged but before discovery was marked complete.
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data.ToArray()));
        ai.Service.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(false);          // the index may even be down now
        var workset = new PipelineSearchWorkset { DiscoverySource = "Ai", LastSearchCursor = AiSearchIdPage.EndCursor, DiscoveredCount = 3, SnapshotMaxRecordId = 999 };

        var o = await RunAsync(data, ai, workset);

        Assert.Null(o.Error);
        Assert.Empty(ai.CursorsAskedFor);
        await ai.Service.DidNotReceive().IsHealthyAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, o.UseSqlCalls);
        Assert.Equal(1, o.CompleteCalls);
        Assert.Empty(o.Sql.TreesSeen);                                                    // nor was the table read
    }

    [Fact]
    public async Task AiReadThatReachedTheEndWithNothingStaged_IsReadFromSql()
    {
        var data = new List<DataRow> { Data(1), Data(2) };
        var ai = new AiFake(AiPage("~", data.ToArray()));
        var workset = new PipelineSearchWorkset { DiscoverySource = "Ai", LastSearchCursor = AiSearchIdPage.EndCursor, DiscoveredCount = 0, SnapshotMaxRecordId = 999 };

        var o = await RunAsync(data, ai, workset);

        Assert.Null(o.Error);
        Assert.Empty(ai.CursorsAskedFor);
        Assert.Equal(1, o.UseSqlCalls);
        Assert.Equal(new long[] { 1, 2 }, o.StagedIds);
    }

    [Fact]
    public async Task MaxResultsAlreadyStaged_RetryDoesNotQueryTheIndexAgain()
    {
        var data = Enumerable.Range(1, 6).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("a", data[0], data[1], data[2]), AiPage("~", data[3], data[4], data[5]));
        var workset = new PipelineSearchWorkset { DiscoverySource = "Ai", LastSearchCursor = "a", DiscoveredCount = 3, SnapshotMaxRecordId = 999 };

        var o = await RunAsync(data, ai, workset, maxResults: 3, streamAbove: 2);

        Assert.Null(o.Error);
        Assert.Empty(ai.CursorsAskedFor);
        Assert.Empty(o.Staged);
        Assert.Equal(1, o.CompleteCalls);
    }

    [Fact]
    public async Task AiWorksetThatCannotBeResumed_AfterStagingRecords_FailsForRetryInsteadOfSwitchingSource()
    {
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data.ToArray()));
        ai.Service.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(false);        // the index is down now
        var workset = new PipelineSearchWorkset { DiscoverySource = "Ai", LastSearchCursor = "a", DiscoveredCount = 2, SnapshotMaxRecordId = 999 };

        var o = await RunAsync(data, ai, workset);

        Assert.IsType<InvalidOperationException>(o.Error ?? o.Error);
        Assert.Equal(0, o.UseSqlCalls);          // switching now would stage records twice or skip some
        Assert.Empty(o.Staged);
    }

    [Fact]
    public async Task AiWorksetThatCannotBeResumed_BeforeStagingAnything_IsSimplyReadFromSql()
    {
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("~", data.ToArray()));
        ai.Service.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(false);
        var workset = new PipelineSearchWorkset { DiscoverySource = "Ai", SnapshotMaxRecordId = 999 };

        var o = await RunAsync(data, ai, workset);

        Assert.Null(o.Error);
        Assert.Equal(1, o.UseSqlCalls);
        Assert.Equal(new long[] { 1, 2, 3 }, o.StagedIds);
        Assert.Equal("Sql", workset.DiscoverySource);
    }

    // ── SQL answers ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IndexFindsNothing_TheSearchIsRunInSqlInstead()
    {
        var data = new List<DataRow> { Data(1), Data(2), Data(3, "Closed") };
        var ai = new AiFake();                     // the index has none of them (e.g. created a moment ago)

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 1, 2 }, o.StagedIds);
        Assert.Equal(1, o.BeginAiCalls);
        Assert.Equal(1, o.UseSqlCalls);
        Assert.True(UsedTheOriginalSqlFilter(o));
        Assert.Equal("Sql", o.Workset.DiscoverySource);
        Assert.Equal(1, o.CompleteCalls);
    }

    [Fact]
    public async Task IndexProposesOnlyRecordsSqlRejects_TheSearchIsRunInSqlInstead()
    {
        var data = new List<DataRow> { Data(1, "Closed"), Data(2) };
        var ai = new AiFake(AiPage("~", data[0]));   // stale: record 1 is no longer Active; record 2 was never indexed

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 2 }, o.StagedIds);
        Assert.Equal(1, o.UseSqlCalls);
    }

    [Fact]
    public async Task IndexFailsBeforeAnythingIsStaged_TheSearchIsRunInSql()
    {
        var data = new List<DataRow> { Data(1), Data(2) };
        var ai = new AiFake().Throwing(() => new InvalidOperationException("Azure Search unavailable"), afterPages: 0);

        var o = await RunAsync(data, ai);

        Assert.Null(o.Error);
        Assert.Equal(new long[] { 1, 2 }, o.StagedIds);
        Assert.Equal(1, o.UseSqlCalls);
    }

    [Fact]
    public async Task IndexFailsAfterRecordsWereStaged_TheFailurePropagatesForRetryAndSqlIsNotMixedIn()
    {
        var data = Enumerable.Range(1, 3).Select(i => Data(i)).ToList();
        var first = AiPage("a", data[0]);
        var ai = new AiFake(first, AiPage("~", data[1], data[2]));
        // First page succeeds, the second read fails.
        ai.Service.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => OnePageThenFail(first));

        var o = await RunAsync(data, ai);

        Assert.NotNull(o.Error);
        Assert.Equal(new long[] { 1 }, o.StagedIds);       // what was staged stays; the retry resumes from cursor "a"
        Assert.Equal(new[] { "a" }, o.AiCursors);
        Assert.Equal(0, o.UseSqlCalls);
        Assert.False(UsedTheOriginalSqlFilter(o));

        static async IAsyncEnumerable<AiSearchIdPage> OnePageThenFail((string Cursor, Guid[] Ids) page)
        {
            await Task.Yield();
            yield return new AiSearchIdPage(page.Ids, page.Cursor);
            throw new InvalidOperationException("Azure Search unavailable");
        }
    }

    [Theory]
    [InlineData(false, false, "is", "Active")]     // not indexed at all (neither searchable nor filterable)
    [InlineData(true, true, "contains", "Active")] // pattern operators are switched off here (the helper default; see the test with them on)
    [InlineData(true, true, "is", "the")]          // a value the index cannot match
    public async Task IneligibleSearch_IsReadFromSql_AndTheIndexIsNeverAsked(bool searchable, bool filterable, string op, string value)
    {
        var data = new List<DataRow> { Data(1), Data(2) };
        var ai = new AiFake(AiPage("~", data.ToArray()));

        var o = await RunAsync(data, ai, searchable: searchable, filterable: filterable, op: op, value: value);

        Assert.Empty(ai.CursorsAskedFor);
        await ai.Service.DidNotReceive().IsHealthyAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, o.BeginAiCalls);
        Assert.Equal(0, o.UseSqlCalls);
        Assert.True(UsedTheOriginalSqlFilter(o));
    }

    [Fact]
    public async Task Contains_WithPatternOperatorsOn_IsNarrowedByTheIndex_AndStagedFromTheSqlRead()
    {
        var data = new List<DataRow> { Data(1), Data(2) };
        var ai = new AiFake(AiPage("~", data.ToArray()));

        var o = await RunAsync(data, ai, op: "contains", value: "Active", patternOperators: true);

        Assert.Null(o.Error);
        Assert.Equal(1, o.BeginAiCalls);
        Assert.NotEmpty(ai.CursorsAskedFor);
        Assert.Equal(new long[] { 1, 2 }, o.StagedIds);
    }

    [Fact]
    public async Task IndexNotConfigured_SqlAnswersAsBefore()
    {
        var data = new List<DataRow> { Data(1), Data(2, "Closed") };

        var o = await RunAsync(data, ai: null);

        Assert.Equal(new long[] { 1 }, o.StagedIds);
        Assert.Equal(0, o.BeginAiCalls);
        Assert.Empty(o.AiCursors);
    }

    [Fact]
    public async Task GridSearchDisabled_SqlAnswers()
    {
        var data = new List<DataRow> { Data(1) };
        var ai = new AiFake(AiPage("~", data.ToArray()));
        ai.Service.IsGridSearchEnabled.Returns(false);

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 1 }, o.StagedIds);
        Assert.Empty(ai.CursorsAskedFor);
        Assert.Equal(0, o.BeginAiCalls);
    }
}
