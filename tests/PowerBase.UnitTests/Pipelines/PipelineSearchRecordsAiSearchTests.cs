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
/// Performance-routing coverage for the Pipeline "search-records" step: when
/// UseAzureAiForGridSearch (IAzureSearchService.IsGridSearchEnabled) is on and the search
/// index is healthy, the step should route through Azure AI Search — the same
/// UseAzureAiForGridSearch flag and OData-filter pattern RunReportQueryHandler already uses
/// for Reports — instead of scanning the table via SQL, then resolve the matched PublicIds
/// back to a plain Id "in" filter for the final row fetch. That final fetch (and the
/// "no MaxResults configured" fallback path) goes through IPipelineRecordSearchService when
/// it's available — not IRecordRepository directly — because it may target a different
/// tenant's connection for cross-tenant pipeline steps.
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
        new() { Id = 7, Fid = 7, Name = "Status", TypeCode = "Text", IsSearchable = true }
    };

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

    private async Task<string> RunAsync(IServiceProvider serviceProvider)
    {
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(Fields());
        var engine = BuildEngine(serviceProvider);
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var step = SearchStep(Config());
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
    public async Task GridSearchEnabled_HealthyIndex_RoutesThroughAzureSearchAndResolvesToIdFilter()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        var matchedPublicId = Guid.NewGuid();
        _azureSearchService.SearchRecordsByFilterAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { matchedPublicId });
        _recordRepo.GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<long> { 42 });

        FilterGroup? capturedFilter = null;
        StubSearchServiceAsync(
            new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["Id"] = 42L, ["PublicId"] = matchedPublicId, ["f_7"] = "Active" } },
            f => capturedFilter = f);

        var json = await RunAsync(Provider());

        await _azureSearchService.Received(1).SearchRecordsByFilterAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(capturedFilter);
        // The final fetch after AI Search resolution must be a plain Id "in" filter, not the
        // original text condition — proves the SQL scan was replaced, not just supplemented.
        Assert.Equal(3, capturedFilter!.Nodes[0].Condition!.FieldId);
        Assert.Equal("in", capturedFilter.Nodes[0].Condition!.Operator);
        Assert.Contains(matchedPublicId.ToString(), json);
    }

    [Fact]
    public async Task GridSearchEnabled_MoreMatchesThanOneSqlChunk_FetchesAllOfThemWithoutTruncating()
    {
        // Regression: an earlier version capped AI Search matches at a fixed 2000 and silently
        // dropped the rest. Matches must now all be fetched via chunked queries instead.
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        const int totalMatches = 20000; // user-reported scenario: far more than one 2000-id SQL chunk
        var publicIds = Enumerable.Range(0, totalMatches).Select(_ => Guid.NewGuid()).ToList();
        _azureSearchService.SearchRecordsByFilterAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(publicIds);
        // GetIdsByPublicIdsAsync is called once per Id-resolution chunk — echo back one Id per input.
        _recordRepo.GetIdsByPublicIdsAsync(_table, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<Guid>>(1).Select((_, i) => (long)i).ToList());

        var callCount = 0;
        _searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                var chunkIds = JsonSerializer.Deserialize<long[]>(ci.ArgAt<FilterGroup?>(3)!.Nodes[0].Condition!.Value!)!;
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = chunkIds
                    .Select(id => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Id"] = id, ["PublicId"] = Guid.NewGuid(), ["f_7"] = "Active" })
                    .ToList();
                return Task.FromResult(rows);
            });

        var json = await RunAsync(Provider());

        Assert.True(callCount >= 10, $"Expected at least 10 chunked fetches for {totalMatches} matches, got {callCount}.");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(totalMatches, doc.RootElement.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task GridSearchEnabled_NoAiMatches_ShortCircuitsToEmptyWithoutMatchingEverything()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        _azureSearchService.SearchRecordsByFilterAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var json = await RunAsync(Provider());

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("records").GetArrayLength());
        // Zero AI matches must short-circuit before any row fetch — never fall through to
        // "no filter" behavior that would return every record in the table.
        await _searchService.DidNotReceive().SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GridSearchEnabled_AiSearchThrows_FallsBackToSqlPath()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(true);
        _azureSearchService.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(true);
        _azureSearchService.SearchRecordsByFilterAsync(Arg.Any<long>(), _table.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Guid>>>(_ => throw new InvalidOperationException("Azure Search unavailable"));

        FilterGroup? capturedFilter = null;
        StubSearchServiceAsync(
            new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["Id"] = 1L, ["PublicId"] = Guid.NewGuid(), ["f_7"] = "Active" } },
            f => capturedFilter = f);

        var json = await RunAsync(Provider());

        // Fell back to the original condition tree (field 7 "is Active"), not an Id filter.
        Assert.NotNull(capturedFilter);
        Assert.Equal(7, capturedFilter!.Nodes[0].Condition!.FieldId);
        Assert.Contains("Active", json);
    }

    [Fact]
    public async Task GridSearchDisabled_NeverCallsAzureSearch()
    {
        _azureSearchService.IsGridSearchEnabled.Returns(false);
        StubSearchServiceAsync(new List<IReadOnlyDictionary<string, object?>>());

        await RunAsync(Provider());

        await _azureSearchService.DidNotReceive().SearchRecordsByFilterAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
