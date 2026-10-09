using System.Reflection;
using System.Text.Json;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Routing coverage for the Pipeline "search-records" step (non-streamed). When UseAzureAiForGridSearch is on and the index
/// is healthy, a search whose every condition is on a plain-text physical field that is both searchable and filterable
/// (see PipelineAiSearchPlanner) is answered through Azure AI Search: every candidate id is paged out of the index (no
/// ceiling), and each candidate is read from SQL with the COMPLETE filter applied, so only records SQL itself matches come
/// back. Anything else - a Formula / Lookup / Summary / relationship field, a field that is not both searchable and
/// filterable, a non-text type, another operator - is read from SQL, and so is a search the index finds nothing for or
/// cannot answer. That fetch goes through IPipelineRecordSearchService, not IRecordRepository directly, because it may target
/// a different tenant's connection for cross-tenant pipeline steps.
/// </summary>
public class PipelineSearchRecordsAiSearchTests
{
    private readonly IRecordRepository _recordRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IPipelineRecordSearchService _searchService;
    private readonly IAzureSearchService _azureSearchService;
    private readonly AppTable _table = new() { Id = 100, PublicId = Guid.NewGuid() };

    private PipelineEngine BuildEngine(IServiceProvider serviceProvider) => new(
        Substitute.For<IPipelineRepository>(),
        _recordRepo,
        Substitute.For<IRecordWriteService>(),
        _tableRepo,
        _fieldRepo,
        Substitute.For<IRelationshipRepository>(),
        Substitute.For<IEmailService>(),
        Substitute.For<IHttpClientFactory>(),
        Substitute.For<IFileStorageService>(),
        Options.Create(new PipelineExecutionOptions()),
        Substitute.For<ILogger<PipelineEngine>>(),
        Substitute.For<IPipelineTriggerInterceptor>(),
        Substitute.For<ITenantUnitOfWork>(),
        Substitute.For<IPipelineAuditFormatter>(),
        Substitute.For<IQueryContext>(),
        Substitute.For<IServiceScopeFactory>(),
        serviceProvider,
        Substitute.For<IAdminRepository>(),
        Substitute.For<ITenantRepository>(),
        Substitute.For<IPipelineStepIdempotencyRepository>());

    public PipelineSearchRecordsAiSearchTests()
    {
        _recordRepo = Substitute.For<IRecordRepository>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _searchService = Substitute.For<IPipelineRecordSearchService>();
        _azureSearchService = Substitute.For<IAzureSearchService>();
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
    }

    private static List<AppField> Fields() => new()
    {
        new() { Id = 7, Fid = 7, Name = "Status", TypeCode = "Text", IsSearchable = true, IsFilterable = true }
    };

    private static async IAsyncEnumerable<AiSearchIdPage> Pages(params AiSearchIdPage[] pages)
    {
        foreach (var page in pages) { yield return page; await Task.Yield(); }
    }

    private void AiPages(params AiSearchIdPage[] pages) =>
        _azureSearchService.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Pages(pages));

    private static Dictionary<string, object?> Row(long id, Guid publicId, string status = "Active") =>
        new() { ["Id"] = id, ["PublicId"] = publicId, ["f_7"] = status };

    private static FilterCondition IdCondition(FilterGroup? filter) =>
        filter!.Nodes.Select(n => n.Condition).First(c => c is { FieldId: 3 })!;

    private static PipelineStep SearchStep(object config) => new()
    {
        Id = 1, RefId = "ref_search", Type = "query", Subtype = "search-records",
        ConfigJson = JsonSerializer.Serialize(config)
    };

    private object Config() => new
    {
        TableId = _table.PublicId.ToString(),
        FilterGroups = new List<object>
        {
            new { LogicalOp = "AND", Rules = new List<object> { new { Field = "fid_7", Operator = "is", Value = "Active" } } }
        }
    };

    private async Task<string> RunAsync(IServiceProvider serviceProvider, object? config = null, List<AppField>? fields = null, IServiceProvider? stepScope = null)
    {
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(fields ?? Fields());
        var engine = BuildEngine(serviceProvider);
        if (stepScope != null)
        {
            // What RunInStepScopeAsync does around a saved-account step: the step's own service scope.
            var local = (AsyncLocal<IServiceProvider?>)typeof(PipelineEngine)
                .GetField("_stepServices", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
            local.Value = stepScope;
        }
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var step = SearchStep(config ?? Config());
        return await (Task<string>)method.Invoke(engine, new object[]
        {
            step, "{}", new Dictionary<string, object>(), new List<PipelineStep> { step }, new Dictionary<string, object>(),
            1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            _recordRepo, _tableRepo, _fieldRepo, Substitute.For<IRecordWriteService>(),
            Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(),
            _searchService, CancellationToken.None
        })!;
    }

    private IServiceProvider Provider()
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IPipelineRecordSearchService)).Returns(_searchService);
        sp.GetService(typeof(IAzureSearchService)).Returns(_azureSearchService);
        return sp;
    }

    /// <summary>Stubs the single-shot fetch IPipelineRecordSearchService.SearchAsync — the AI
    /// Search resolution path and the "no MaxResults" fallback both call it with
    /// maxResults: null (its native "return everything matching" mode, not a page loop).</summary>
    private void StubSearchServiceAsync(List<IReadOnlyDictionary<string, object?>> rows, Action<FilterGroup?>? onCapture = null)
    {
        _searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                onCapture?.Invoke(ci.ArgAt<FilterGroup?>(3));
                return Task.FromResult((IReadOnlyList<IReadOnlyDictionary<string, object?>>)rows);
            });
    }

    [Fact]
    public async Task GridSearchEnabled_HealthyIndex_RoutesThroughAzureSearchAndVerifiesCandidatesInSql()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var matchedPublicId = Guid.NewGuid();
        AiPages(new AiSearchIdPage([matchedPublicId], "~"));
        _recordRepo.GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<long> { 42 });

        FilterGroup? capturedFilter = null;
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>> { Row(42L, matchedPublicId) }, f => capturedFilter = f);

        var json = await RunAsync(Provider());

        _azureSearchService.Received(1).SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _azureSearchService.DidNotReceive().SearchRecordsByFilterAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(capturedFilter);
        // The SQL read is restricted to the candidates AND still carries the original condition: AI Search only proposes.
        Assert.Equal("in", IdCondition(capturedFilter).Operator);
        Assert.Contains(capturedFilter!.Nodes, n => n.Condition is { FieldId: 7 });
        Assert.Contains(matchedPublicId.ToString(), json);
    }

    [Fact]
    public async Task GridSearchEnabled_FilterIsBuiltAsCaseInsensitivePhraseMatchOnThePhysicalColumn()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        AiPages();
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>>());

        await RunAsync(Provider());

        _azureSearchService.Received(1).SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), _table.Id,
            "search.ismatch('\"Active\"', 'f_7', 'full', 'any')", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GridSearchEnabled_MoreMatchesThanOnePageOrSqlChunk_FetchesAllOfThemWithoutTruncating()
    {
        // Regression: an earlier version capped AI Search matches (50,000 in the service, 2,000 in the engine) and silently
        // dropped the rest. Every page must be read and every candidate fetched, via chunked queries.
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        const int pageCount = 60, perPage = 1000;                         // 60,000: past the old 50,000 service cap
        long nextRecordId = 0;
        var pages = Enumerable.Range(0, pageCount)
            .Select(_ => new AiSearchIdPage(Enumerable.Range(0, perPage).Select(_ => Guid.NewGuid()).ToList(), "x"))
            .ToArray();
        AiPages(pages);
        _recordRepo.GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Select(_ => Interlocked.Increment(ref nextRecordId)).ToList());

        var callCount = 0;
        var largestIdList = 0;
        _searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                var chunkIds = JsonSerializer.Deserialize<long[]>(IdCondition(ci.ArgAt<FilterGroup?>(3)).Value!)!;
                largestIdList = Math.Max(largestIdList, chunkIds.Length);
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = chunkIds
                    .Select(id => (IReadOnlyDictionary<string, object?>)Row(id, Guid.NewGuid()))
                    .ToList();
                return Task.FromResult(rows);
            });

        var json = await RunAsync(Provider());

        Assert.Equal(pageCount, callCount);
        Assert.True(largestIdList <= PipelineEngine.AiCandidateRecordIdChunkSize, $"an id list of {largestIdList} leaves no room under SQL Server's 2,100 parameters");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(pageCount * perPage, doc.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task GridSearchEnabled_AnOversizedPageOfCandidates_IsSentToSqlInBoundedChunks()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        AiPages(new AiSearchIdPage(Enumerable.Range(0, 2500).Select(_ => Guid.NewGuid()).ToList(), "~"));
        long next = 0;
        _recordRepo.GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Select(_ => ++next).ToList());
        var sizes = new List<int>();
        _searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = JsonSerializer.Deserialize<long[]>(IdCondition(ci.ArgAt<FilterGroup?>(3)).Value!)!;
                sizes.Add(ids.Length);
                return Task.FromResult((IReadOnlyList<IReadOnlyDictionary<string, object?>>)ids.Select(i => (IReadOnlyDictionary<string, object?>)Row(i, Guid.NewGuid())).ToList());
            });

        var json = await RunAsync(Provider());

        Assert.Equal(new[] { 1000, 1000, 500 }, sizes);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2500, doc.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task GridSearchEnabled_TheIndexAndTenantComeFromTheStepsOwnScope_NotTheFlowOwners()
    {
        // A step that runs through a saved account reads ANOTHER tenant's table: its tenant id and index are the scope's.
        _azureSearchService.IsGridSearchEnabled.Returns(true);              // the flow owner's service must stay untouched
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var scopeAzure = Substitute.For<IAzureSearchService>();
        scopeAzure.IsGridSearchEnabled.Returns(true);
        scopeAzure.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        scopeAzure.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Pages(new AiSearchIdPage([Guid.NewGuid()], "~")));
        var scopeQuery = Substitute.For<IQueryContext>();
        scopeQuery.TenantId.Returns(77L);
        var scope = Substitute.For<IServiceProvider>();
        scope.GetService(typeof(IAzureSearchService)).Returns(scopeAzure);
        scope.GetService(typeof(IQueryContext)).Returns(scopeQuery);
        _recordRepo.GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<long> { 1 });
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>> { Row(1L, Guid.NewGuid()) });

        await RunAsync(Provider(), stepScope: scope);

        scopeAzure.Received(1).SearchRecordIdsByFilterPagedAsync(77L, _table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        _azureSearchService.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _azureSearchService.DidNotReceive().IsHealthyAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GridSearchDisabledInTheStepsScope_SqlAnswers_EvenIfTheFlowOwnersServiceIsEnabled()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var scopeAzure = Substitute.For<IAzureSearchService>();
        scopeAzure.IsGridSearchEnabled.Returns(false);
        var scope = Substitute.For<IServiceProvider>();
        scope.GetService(typeof(IAzureSearchService)).Returns(scopeAzure);
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>> { Row(1L, Guid.NewGuid()) });

        var json = await RunAsync(Provider(), stepScope: scope);

        scopeAzure.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        _azureSearchService.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Contains("Active", json);
    }

    [Fact]
    public async Task GridSearchEnabled_NoAiMatches_ChecksSqlInsteadOfReturningEmpty()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        AiPages();   // the index has nothing (e.g. the record was created a moment ago and is not indexed yet)
        var publicId = Guid.NewGuid();
        FilterGroup? capturedFilter = null;
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>> { Row(5L, publicId) }, f => capturedFilter = f);

        var json = await RunAsync(Provider());

        // SQL was asked with the original condition, and its record is returned.
        Assert.NotNull(capturedFilter);
        Assert.Equal(7, capturedFilter!.Nodes[0].Condition!.FieldId);
        Assert.Contains(publicId.ToString(), json);
    }

    [Fact]
    public async Task GridSearchEnabled_AiSearchThrows_FallsBackToSqlPath()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        _azureSearchService.SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingPages());

        FilterGroup? capturedFilter = null;
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>> { Row(1L, Guid.NewGuid()) }, f => capturedFilter = f);

        var json = await RunAsync(Provider());

        // Fell back to the original condition tree (field 7 "is Active"), not an Id filter.
        Assert.NotNull(capturedFilter);
        Assert.Equal(7, capturedFilter!.Nodes[0].Condition!.FieldId);
        Assert.Contains("Active", json);

        static async IAsyncEnumerable<AiSearchIdPage> ThrowingPages()
        {
            await Task.Yield();
            throw new InvalidOperationException("Azure Search unavailable");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task GridSearchEnabled_CandidateSqlDoesNotMatch_IsNotReturned()
    {
        // A record deleted or changed since it was indexed: the index proposes it, SQL (with the full filter) does not return it.
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var stale = Guid.NewGuid();
        var good = Guid.NewGuid();
        AiPages(new AiSearchIdPage([stale, good], "~"));
        _recordRepo.GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<long> { 1, 2 });
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>> { Row(2L, good) });   // SQL only matches record 2

        var json = await RunAsync(Provider());

        Assert.Contains(good.ToString(), json);
        Assert.DoesNotContain(stale.ToString(), json);
    }

    [Fact]
    public async Task GridSearchEnabled_MaxResults_StopsPagingOnceEnoughVerifiedRows()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var p1 = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var p2 = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        AiPages(new AiSearchIdPage(p1, "a"), new AiSearchIdPage(p2, "b"));
        long id = 0;
        _recordRepo.GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Select(_ => ++id).ToList());
        _searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult((IReadOnlyList<IReadOnlyDictionary<string, object?>>)
                JsonSerializer.Deserialize<long[]>(IdCondition(ci.ArgAt<FilterGroup?>(3)).Value!)!
                    .Select(i => (IReadOnlyDictionary<string, object?>)Row(i, Guid.NewGuid())).ToList()));
        var config = new
        {
            TableId = _table.PublicId.ToString(),
            MaxResults = 2,
            FilterGroups = new List<object> { new { LogicalOp = "AND", Rules = new List<object> { new { Field = "fid_7", Operator = "is", Value = "Active" } } } }
        };

        var json = await RunAsync(Provider(), config);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("records").GetArrayLength());
        await _recordRepo.Received(1).GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());   // the second page was never read
    }

    public static IEnumerable<object[]> SearchesThatMustStayInSql()
    {
        AppField Text(string type, bool searchable = true, bool filterable = true) =>
            new() { Id = 7, Fid = 7, Name = "Status", TypeCode = type, IsSearchable = searchable, IsFilterable = filterable };

        yield return new object[] { "not searchable", Text("Text", searchable: false), "is" };
        yield return new object[] { "not filterable", Text("Text", filterable: false), "is" };
        yield return new object[] { "not indexed", Text("Text", false, false), "is" };
        yield return new object[] { "formula", Text("Formula_Text"), "is" };
        yield return new object[] { "lookup", Text("Lookup"), "is" };
        yield return new object[] { "summary", Text("Summary"), "is" };
        yield return new object[] { "reference", Text("Reference"), "is" };
        yield return new object[] { "number", Text("Number"), "is" };
        yield return new object[] { "date", Text("Date"), "is" };
        yield return new object[] { "contains operator", Text("Text"), "contains" };
    }

    [Theory]
    [MemberData(nameof(SearchesThatMustStayInSql))]
    public async Task GridSearchEnabled_IneligibleSearch_IsAnsweredFromSqlAndNeverTouchesTheIndex(string scenario, AppField field, string op)
    {
        _ = scenario;
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>>());
        var config = new
        {
            TableId = _table.PublicId.ToString(),
            FilterGroups = new List<object> { new { LogicalOp = "AND", Rules = new List<object> { new { Field = "fid_7", Operator = op, Value = "Active" } } } }
        };

        await RunAsync(Provider(), config, new List<AppField> { field });

        _azureSearchService.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _azureSearchService.DidNotReceive().IsHealthyAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GridSearchEnabled_OneIneligibleConditionAmongEligibleOnes_SendsTheWholeSearchToSql()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var fields = new List<AppField>
        {
            new() { Id = 7, Fid = 7, Name = "Status", TypeCode = "Text", IsSearchable = true, IsFilterable = true },
            new() { Id = 8, Fid = 8, Name = "Order no", TypeCode = "Formula_Text", IsSearchable = true, IsFilterable = true }
        };
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>>());
        var config = new
        {
            TableId = _table.PublicId.ToString(),
            FilterGroups = new List<object>
            {
                new { LogicalOp = "AND", Rules = new List<object>
                {
                    new { Field = "fid_7", Operator = "is", Value = "Active" },
                    new { Field = "fid_8", Operator = "is", Value = "ORD1" }
                } }
            }
        };

        await RunAsync(Provider(), config, fields);

        _azureSearchService.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GridSearchEnabled_UnhealthyIndex_IsAnsweredFromSql()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(false);
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>> { Row(1L, Guid.NewGuid()) });

        var json = await RunAsync(Provider());

        _azureSearchService.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Contains("Active", json);
    }

    [Fact]
    public async Task GridSearchDisabled_NeverCallsAzureSearch()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(false);
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>>());

        await RunAsync(Provider());

        _azureSearchService.DidNotReceive().SearchRecordIdsByFilterPagedAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LegacyEditorGroupSavedAsOr_ExecutesVisibleRulesAsAnd()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(false);
        FilterGroup? capturedFilter = null;
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>>(), f => capturedFilter = f);
        var legacyConfig = new
        {
            TableId = _table.PublicId.ToString(),
            FilterGroups = new List<object>
            {
                new
                {
                    LogicalOp = "OR",
                    Rules = new List<object>
                    {
                        new { Field = "fid_7", Operator = "is", Value = "a" },
                        new { Field = "fid_7", Operator = "is", Value = "b" }
                    }
                }
            }
        };

        await RunAsync(Provider(), legacyConfig);

        Assert.NotNull(capturedFilter);
        Assert.Equal("and", capturedFilter!.Logic);
        Assert.Equal(2, capturedFilter.Nodes.Count);
    }

    [Fact]
    public async Task NestedEditorGroups_ExecuteAsAndRulesInsideOrAlternatives()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(false);
        FilterGroup? capturedFilter = null;
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>>(), f => capturedFilter = f);
        var config = new
        {
            TableId = _table.PublicId.ToString(),
            FilterGroups = new List<object>
            {
                new
                {
                    LogicalOp = "OR", // legacy editor value; visible root rules are AND
                    Rules = new List<object>
                    {
                        new { Type = "rule", Field = "fid_7", Operator = "is", Value = "root" },
                        new
                        {
                            Type = "nested",
                            Groups = new List<object>
                            {
                                new
                                {
                                    Rules = new List<object>
                                    {
                                        new { Type = "rule", Field = "fid_7", Operator = "is", Value = "left-1" },
                                        new { Type = "rule", Field = "fid_7", Operator = "is", Value = "left-2" }
                                    }
                                },
                                new
                                {
                                    Rules = new List<object>
                                    {
                                        new { Type = "rule", Field = "fid_7", Operator = "is", Value = "right" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        await RunAsync(Provider(), config);

        Assert.NotNull(capturedFilter);
        Assert.Equal("and", capturedFilter!.Logic);
        Assert.Equal(2, capturedFilter.Nodes.Count);
        var nestedOr = capturedFilter.Nodes[1].Group;
        Assert.NotNull(nestedOr);
        Assert.Equal("or", nestedOr!.Logic);
        Assert.Equal(2, nestedOr.Nodes.Count);
        Assert.All(nestedOr.Nodes, node => Assert.Equal("and", node.Group!.Logic));
        Assert.Equal(2, nestedOr.Nodes[0].Group!.Nodes.Count);
        Assert.Single(nestedOr.Nodes[1].Group!.Nodes);
    }
}
