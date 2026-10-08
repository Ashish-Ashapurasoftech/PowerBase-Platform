using System.Data;
using System.Reflection;
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
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// Search Records has no ceiling on how many records it may match or how large its output is: nothing is refused for
/// being big. (It used to fail past 10,000 records and past a 5 MB output.)
/// </summary>
public class PipelineUnlimitedSearchTests
{
    private static readonly Guid TableId = Guid.NewGuid();

    private sealed class PagedStub(int pages, int pageSize, int textLength = 0, int searchAsyncRows = 0) : IPipelineRecordSearchService, IKeysetPipelineRecordSearchService
    {
        public int PagesServed { get; private set; }
        public bool SupportsKeysetPaging => true;
        public Task<long> GetMaxRecordIdAsync(AppTable table, CancellationToken ct = default) => Task.FromResult(long.MaxValue);
        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchPagesAsync(
            AppTable table, IReadOnlyList<AppField> fields, int size, FilterGroup? filterTree = null,
            long afterId = 0, long maxId = long.MaxValue, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var p = 0; p < pages; p++)
            {
                PagesServed++;
                yield return Enumerable.Range(0, pageSize).Select(i => Row(p * pageSize + i + 1, textLength)).ToList();
                await Task.Yield();
            }
        }
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchAsync(AppTable table, IReadOnlyList<AppField> fields,
            int? maxResults = null, FilterGroup? filterTree = null, CancellationToken ct = default, int page = 1) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                Enumerable.Range(0, maxResults is { } m ? Math.Min(m, searchAsyncRows) : searchAsyncRows).Select(i => Row(i + 1, textLength)).ToList());
        public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCopySnapshotAsync(
            AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
    }

    private static IReadOnlyDictionary<string, object?> Row(long id, int textLength) =>
        new Dictionary<string, object?> { ["Id"] = id, ["PublicId"] = Guid.NewGuid(), ["f_6"] = new string('x', textLength) };

    private static async Task<JsonElement> SearchAsync(PipelineExecutionOptions options, IPipelineRecordSearchService search, int? maxResults = null)
    {
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var table = new AppTable { Id = 100, AppId = 1, PublicId = TableId };
        tableRepo.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(100, Arg.Any<CancellationToken>()).Returns(new List<AppField> { new() { Id = 6, Fid = 6, Name = "Name", TypeCode = "Text" } });
        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(search);
        serviceProvider.GetService(typeof(IPipelineApiRequestDispatcher)).Returns(Substitute.For<IPipelineApiRequestDispatcher>());
        var idempotency = Substitute.For<IPipelineStepIdempotencyRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var writeService = Substitute.For<IRecordWriteService>();
        var interceptor = Substitute.For<IPipelineTriggerInterceptor>();
        var engine = new PipelineEngine(
            Substitute.For<IPipelineRepository>(), recordRepo, writeService, tableRepo, fieldRepo,
            Substitute.For<IRelationshipRepository>(), Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(), Options.Create(options),
            Substitute.For<ILogger<PipelineEngine>>(), interceptor, uow, Substitute.For<IPipelineAuditFormatter>(),
            Substitute.For<IQueryContext>(), Substitute.For<IServiceScopeFactory>(), serviceProvider,
            Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), idempotency);

        var config = maxResults.HasValue
            ? $"{{\"TableId\":\"{TableId}\",\"MaxResults\":{maxResults},\"FilterGroups\":[]}}"
            : $"{{\"TableId\":\"{TableId}\",\"FilterGroups\":[]}}";
        var step = new PipelineStep { Id = 1, RefId = "ref_search", Type = "query", Subtype = "search-records", ConfigJson = config };
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            var json = await (Task<string>)method.Invoke(engine, new object[]
            {
                step, "{}", new Dictionary<string, object>(), new List<PipelineStep> { step }, new Dictionary<string, object>(),
                1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
                recordRepo, tableRepo, fieldRepo, writeService, interceptor, uow, idempotency,
                Substitute.For<IFileStorageService>(), search, CancellationToken.None
            })!;
            return JsonDocument.Parse(json).RootElement;
        }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    [Fact]
    public async Task ASearchBeyondTheStreamingThreshold_WithNoLoop_ReturnsEveryRecord_NotAnError()
    {
        // Threshold 5, 3 pages of 10: 30 matches. This used to throw "matched more than the configured materialization limit".
        var options = new PipelineExecutionOptions { SearchRecordsPageSize = 5, StreamSearchAboveRecords = 5 };
        var search = new PagedStub(pages: 3, pageSize: 10);

        var output = await SearchAsync(options, search);

        Assert.Equal(30, output.GetProperty("records").GetArrayLength());
        Assert.Equal(3, search.PagesServed);
    }

    [Fact]
    public async Task ManyPages_AreReadToTheEnd_NothingIsDropped()
    {
        var options = new PipelineExecutionOptions { SearchRecordsPageSize = 500, StreamSearchAboveRecords = 10_000 };
        var search = new PagedStub(pages: 60, pageSize: 500);   // 30,000 records: three times the old ceiling

        var output = await SearchAsync(options, search);

        Assert.Equal(30_000, output.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task AnExplicitMaxResultsAboveTheThreshold_IsHonouredAndNeverRefused()
    {
        var options = new PipelineExecutionOptions { SearchRecordsPageSize = 5, StreamSearchAboveRecords = 5 };
        var search = new PagedStub(pages: 0, pageSize: 0, searchAsyncRows: 40);

        var output = await SearchAsync(options, search, maxResults: 40);     // used to throw "cannot materialize more than 5 records"

        Assert.Equal(40, output.GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task ALargeOutput_IsNotRefused_ByDefault()
    {
        // 2,100 records of ~3 KB each is ~6.5 MB of output: past the old fixed 5 MB cap.
        var options = new PipelineExecutionOptions();
        var search = new PagedStub(pages: 1, pageSize: 2_100, textLength: 3_000);

        var output = await SearchAsync(options, search);

        Assert.Equal(2_100, output.GetProperty("records").GetArrayLength());
        Assert.Equal(0, options.MaxStepOutputBytes);
    }

    [Fact]
    public async Task AnOutputCap_IsStillAvailable_WhenSomeoneConfiguresOne()
    {
        var options = new PipelineExecutionOptions { MaxStepOutputBytes = 10_000 };
        var search = new PagedStub(pages: 1, pageSize: 100, textLength: 1_000);

        var ex = await Assert.ThrowsAsync<PipelineNonRetryableException>(() => SearchAsync(options, search));

        Assert.Contains("exceeds the configured maximum", ex.Message);
    }

    // ───────────────────────── configuration ─────────────────────────

    [Fact]
    public void Defaults_DoNotCapAnything()
    {
        var options = new PipelineExecutionOptions();
        Assert.Equal(0, options.MaxStepOutputBytes);
        Assert.True(options.StreamSearchAboveRecords >= options.SearchRecordsPageSize);
    }

    [Theory]
    [InlineData(0, true)]            // unlimited output
    [InlineData(1_000_000_000, true)]
    [InlineData(-1, false)]
    public void Validator_OutputCap_ZeroMeansUnlimited(int bytes, bool valid)
    {
        var options = new PipelineExecutionOptions { MaxStepOutputBytes = bytes };
        if (valid) PipelineExecutionOptionsValidator.Validate(options);
        else Assert.Throws<InvalidOperationException>(() => PipelineExecutionOptionsValidator.Validate(options));
    }

    [Fact]
    public void Validator_AcceptsAVeryLargeStreamingThreshold()
        => PipelineExecutionOptionsValidator.Validate(new PipelineExecutionOptions { StreamSearchAboveRecords = int.MaxValue });
}
