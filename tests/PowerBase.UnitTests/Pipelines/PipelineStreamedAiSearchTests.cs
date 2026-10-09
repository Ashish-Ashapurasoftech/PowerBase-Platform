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
        public int BeginAiCalls, UseSqlCalls, CompleteCalls;
        public SqlStub Sql { get; init; } = null!;
        public PipelineSearchWorkset Workset { get; init; } = null!;
        public Exception? Error { get; set; }
        public List<long> StagedIds => Staged.Select(r => JsonDocument.Parse(r.AfterValuesJson!).RootElement.GetProperty("Id").GetInt64()).ToList();
    }

    private static DataRow Data(long id, string status = "Active") => new(id, Guid.NewGuid(), status);

    private static (string Cursor, Guid[] Ids) AiPage(string cursor, params DataRow[] rows) => (cursor, rows.Select(r => r.PublicId).ToArray());

    private static async Task<Outcome> RunAsync(
        List<DataRow> data, AiFake? ai, PipelineSearchWorkset? workset = null, bool searchable = true, bool filterable = true,
        bool encrypted = false, int? maxResults = null, int? streamAbove = null, string op = "is", string value = "Active", long snapshotMax = 999)
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
            new() { Id = 6, Fid = 6, Name = "Order no", TypeCode = "Text", IsSearchable = searchable, IsFilterable = filterable, IsEncrypted = encrypted }
        });
        recordRepo.GetIdsByPublicIdsAsync(table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<long>>(
                data.Where(r => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Contains(r.PublicId)).Select(r => r.Id).ToList()));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(sql);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        if (ai != null) serviceProvider.GetService(typeof(IAzureSearchService)).Returns(ai.Service);

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var options = new PipelineExecutionOptions();
        if (streamAbove.HasValue) options.StreamSearchAboveRecords = streamAbove.Value;
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
        pipelineRepo.CompleteSearchWorksetDiscoveryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { outcome.CompleteCalls++; return Task.CompletedTask; });
        pipelineRepo.GetPendingSearchWorksetPageAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        Assert.Equal(new[] { "b", "~" }, o.AiCursors);
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
        Assert.Equal(new[] { "a", "~" }, o.AiCursors);
    }

    [Fact]
    public async Task PageWhoseCandidatesAllFailVerification_AdvancesTheCursorAndStagesNothing()
    {
        var data = new List<DataRow> { Data(1, "Closed"), Data(2) };
        var ai = new AiFake(AiPage("a", data[0]), AiPage("~", data[1]));

        var o = await RunAsync(data, ai);

        Assert.Equal(new long[] { 2 }, o.StagedIds);
        Assert.Equal(new[] { "a", "~" }, o.AiCursors);
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
        Assert.Equal(pageCount, o.AiCursors.Count);
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
    public async Task MaxResults_StopsPagingOnceTheLimitIsStaged()
    {
        var data = Enumerable.Range(1, 6).Select(i => Data(i)).ToList();
        var ai = new AiFake(AiPage("a", data[0], data[1]), AiPage("b", data[2], data[3]), AiPage("~", data[4], data[5]));

        var o = await RunAsync(data, ai, maxResults: 3, streamAbove: 2);

        Assert.Equal(new long[] { 1, 2, 3 }, o.StagedIds);
        Assert.Equal(2, ai.PagesServed);                      // the third page was never asked for
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
    [InlineData(false, true, "is", "Active")]      // not searchable
    [InlineData(true, false, "is", "Active")]      // not filterable
    [InlineData(true, true, "contains", "Active")] // an operator the index cannot answer exactly
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
