using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
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
using PowerBase.Domain.Entities;
using Xunit.Abstractions;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineLoopBatchTests(ITestOutputHelper output)
{
    private sealed class Harness : IDisposable
    {
        public readonly IPipelineRepository Repository = Substitute.For<IPipelineRepository>();
        public readonly ConcurrentBag<PipelineStepRun> History = new();
        public readonly ConcurrentBag<string> Written = new();
        public readonly ConcurrentBag<string> Errors = new();
        public readonly ConcurrentBag<IQueryContext> Contexts = new();
        public readonly ConcurrentDictionary<Guid, byte> Checkpointed = new();
        public readonly ServiceProvider Services;
        public readonly Guid TableId = Guid.NewGuid();
        public readonly PipelineExecutionOptions Options;
        public int Active, Peak, DelayMs = 5, FailAt = -1;
        private long _stepId;

        public Harness(int concurrency = 4, int hostConcurrency = 16, bool automatic = false)
        {
            Options = new() { EnableLoopBatches = true, AutoScaleLoopWorkers = automatic, LoopConcurrency = concurrency, PerInstanceLoopConcurrency = hostConcurrency };
            Repository.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true });
            Repository.UpdateStepRunAsync(Arg.Any<PipelineStepRun>(), Arg.Any<CancellationToken>()).Returns(call => {
                var row = call.Arg<PipelineStepRun>();
                if (row.Status == "Failed") Errors.Add(row.OutputContext ?? row.LogMessage ?? "Unknown");
                return Task.CompletedTask;
            });
            Repository.CreateStepRunAsync(Arg.Any<PipelineStepRun>(), Arg.Any<CancellationToken>()).Returns(call => {
                var row = call.Arg<PipelineStepRun>();
                row.Id = Interlocked.Increment(ref _stepId);
                History.Add(row);
                return row.Id;
            });
            var services = new ServiceCollection();
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
            services.AddSingleton(Repository);
            services.AddSingleton<PipelineLoopWorkerPool>();
            services.AddScoped<IPipelineEngine, PipelineEngine>();
            services.AddScoped<IQueryContext>(_ => {
                var query = Substitute.For<IQueryContext>();
                query.TenantId.Returns(42L);
                query.UserId.Returns(7L);
                query.IsUserToken.Returns(true);
                query.AllowedAppIds.Returns(new HashSet<long> { 8 });
                Contexts.Add(query);
                return query;
            });
            services.AddScoped<ITenantUnitOfWork>(_ => Substitute.For<ITenantUnitOfWork>());
            services.AddScoped<IRecordRepository>(_ => {
                var records = Substitute.For<IRecordRepository>();
                records.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(),
                    Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>(), null)
                    .Returns(async call => {
                        var active = Interlocked.Increment(ref Active);
                        int seen;
                        do { seen = Peak; } while (active > seen && Interlocked.CompareExchange(ref Peak, active, seen) != seen);
                        try
                        {
                            await Task.Delay(DelayMs, call.Arg<CancellationToken>());
                            var mapped = call.Arg<IReadOnlyDictionary<long, object?>>()[6]?.ToString() ?? "";
                            if (mapped == FailAt.ToString()) throw new InvalidOperationException("Injected record failure");
                            Written.Add(mapped);
                            return Guid.NewGuid();
                        }
                        finally { Interlocked.Decrement(ref Active); }
                    });
                return records;
            });
            var tables = Substitute.For<IAppTableRepository>();
            tables.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 1, PublicId = TableId });
            services.AddSingleton(tables);
            var fields = Substitute.For<IAppFieldRepository>();
            fields.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(new List<AppField> {
                new() { Id = 6, Fid = 6, Name = "name", TypeCode = "text" }
            });
            services.AddSingleton(fields);
            services.AddSingleton(Substitute.For<IRecordWriteService>());
            services.AddSingleton(Substitute.For<IRelationshipRepository>());
            services.AddSingleton(Substitute.For<IEmailService>());
            services.AddSingleton(Substitute.For<IHttpClientFactory>());
            services.AddSingleton(Substitute.For<IFileStorageService>());
            services.AddSingleton(Substitute.For<ILogger<PipelineEngine>>());
            services.AddSingleton(Substitute.For<IPipelineTriggerInterceptor>());
            services.AddSingleton(Substitute.For<IPipelineAuditFormatter>());
            services.AddSingleton(Substitute.For<IAdminRepository>());
            services.AddSingleton(Substitute.For<ITenantRepository>());
            services.AddSingleton(Substitute.For<IPipelineStepIdempotencyRepository>());
            services.AddSingleton(Substitute.For<IPipelineRecordSearchService>());
            Services = services.BuildServiceProvider();
        }

        // Staged source (a streamed Search Records workset): rows live in a fake table, statuses 0 pending / 1 done / 2 retry / 3 skipped.
        public readonly ConcurrentDictionary<long, byte> Status = new();
        public Exception? InfrastructureFailure;

        private void StageWorkset(Guid worksetId, int count)
        {
            var rows = Enumerable.Range(1, count).Select(i => new PipelineBulkEventRecord {
                Id = i, BulkEventId = worksetId, SearchWorksetId = worksetId, Ordinal = i, RecordPublicId = Guid.NewGuid(),
                EventType = "Added", AfterValuesJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = (i - 1).ToString() })
            }).ToList();
            foreach (var r in rows) Status[r.Id] = 0;
            Repository.GetPendingSearchWorksetPageAsync(worksetId, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlyList<PipelineBulkEventRecord>>(
                    rows.Where(r => Status[r.Id] is 0 or 2).Take(call.ArgAt<int>(1)).ToList()));
            Repository.MarkSearchWorksetRecordsProcessedAsync(worksetId, Arg.Any<List<long>>(), Arg.Any<byte>(), Arg.Any<CancellationToken>())
                .Returns(call => { foreach (var id in call.ArgAt<List<long>>(1)) Status[id] = call.ArgAt<byte>(2); return Task.CompletedTask; });
        }

        public async Task RunAsync(int count, string? mapping = null, CancellationToken ct = default, int? maxConcurrency = null, bool staged = false)
        {
            using var scope = Services.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IPipelineEngine>();
            var loop = new PipelineStep { Id = 2, PipelineId = 1, PublicId = Guid.NewGuid(), RefId = "b", Type = "action", Subtype = "loop",
                ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "a", MaxConcurrency = maxConcurrency }) };
            var child = new PipelineStep { Id = 3, PipelineId = 1, PublicId = Guid.NewGuid(), RefId = "c", Type = "action", Subtype = "create-record",
                ParentStepId = 2, ParentBranch = "children", ConfigJson = JsonSerializer.Serialize(new {
                    TableId, FieldMappings = new[] { new { Field = "fid_6", Value = mapping ?? "{{b.item.value}}" } }
                }) };
            var steps = new Dictionary<string, object> { ["a"] = Enumerable.Range(0, count)
                .Select(i => new Dictionary<string, object> { ["value"] = i.ToString() }).ToList() };
            if (staged)
            {
                var worksetId = Guid.NewGuid();
                StageWorkset(worksetId, count);
                steps["a"] = JsonSerializer.Serialize(new { mode = "chunked-search", worksetId, count });
            }
            var context = new Dictionary<string, object> { ["steps"] = steps, ["_MessageId"] = Guid.NewGuid(), ["_CreatedBy"] = 7L };
            await (Task)typeof(PipelineEngine).GetMethod("ExecuteSiblingStepsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(engine, new object?[] { 1L, new List<PipelineStep> { loop, child }, null, null, context, steps,
                    new List<PipelineEngine.RawStepAuditSnapshot>(), "root", ct })!;
        }
        public void Dispose() => Services.Dispose();
    }

    [Fact]
    public async Task ThousandRecords_UseTenBatches_IsolatedWorkers_AndUniqueHistorySequences()
    {
        using var h = new Harness();
        var timer = Stopwatch.StartNew();
        await h.RunAsync(1000);
        output.WriteLine($"1000 records, 4 workers, simulated write latency {h.DelayMs}ms: {timer.ElapsedMilliseconds}ms");
        Assert.Equal(1000, h.Written.Count);
        Assert.Equal(1000, h.Written.Distinct().Count());
        Assert.InRange(h.Peak, 2, 4);
        var batches = h.History.Where(row => row.StepSubtypeSnapshot == "loop-batch").ToList();
        Assert.Equal(10, batches.Count);
        Assert.All(batches, batch => Assert.Equal("Success", batch.Status));
        Assert.Equal(h.History.Count, h.History.Select(row => row.SequenceNumber).Distinct().Count());
        Assert.Equal(0, h.Active);
        Assert.InRange(h.Services.GetRequiredService<IAppTableRepository>().ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == "GetByPublicIdAsync"), 1, 40);
        foreach (var query in h.Contexts.Where(q => q.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "SetTenantId")))
        {
            query.Received().SetTenantId(42);
            query.Received().SetTokenScope(true, false, Arg.Is<IReadOnlySet<long>>(ids => ids.Contains(8)));
        }
    }

    [Fact]
    public async Task MultiplePipelines_ShareHostWorkerLimit()
    {
        using var h = new Harness(4, 4);
        await Task.WhenAll(h.RunAsync(120), h.RunAsync(120), h.RunAsync(120));
        Assert.Equal(360, h.Written.Count);
        Assert.InRange(h.Peak, 2, 4);
        Assert.Equal(0, h.Active);
    }

    [Fact]
    public async Task AutomaticMode_EngineUsesResolvedCapacityInBatchHistory()
    {
        using var h = new Harness(16, 32, automatic: true);
        await h.RunAsync(40);
        var capacity = h.Services.GetRequiredService<PipelineLoopWorkerPool>();
        var batch = Assert.Single(h.History, row => row.StepSubtypeSnapshot == "loop-batch");
        using var input = JsonDocument.Parse(batch.InputContext!);
        Assert.Equal(capacity.LoopConcurrency, input.RootElement.GetProperty("Workers").GetInt32());
        Assert.Equal(40, h.Written.Count);
        Assert.InRange(h.Peak, 1, capacity.LoopConcurrency);
    }

    [Fact]
    public async Task ExplicitSequentialMode_PreservesOrder()
    {
        using var h = new Harness();
        await h.RunAsync(25, maxConcurrency: 1);
        Assert.True(h.Errors.IsEmpty, string.Join("\n", h.Errors.Take(2)));
        Assert.Equal(1, h.Peak);
        Assert.Equal(25, h.Written.Count);
    }

    [Fact]
    public async Task PreviousIterationReference_DisablesAutomaticParallelism()
    {
        using var h = new Harness();
        await h.RunAsync(5, "{{c.fid_6}}");
        var batch = Assert.Single(h.History, row => row.StepSubtypeSnapshot == "loop-batch");
        using var input = JsonDocument.Parse(batch.InputContext!);
        Assert.Equal(1, input.RootElement.GetProperty("Workers").GetInt32());
    }

    [Theory]
    [InlineData(0, 4, 16)]
    [InlineData(100, 0, 16)]
    [InlineData(100, 8, 4)]
    public void InvalidWorkerSettings_AreRejected(int batch, int concurrency, int host)
    {
        Assert.Throws<InvalidOperationException>(() => PipelineExecutionOptionsValidator.Validate(new() {
            LoopBatchSize = batch, LoopConcurrency = concurrency, PerInstanceLoopConcurrency = host
        }));
    }

    [Fact]
    public async Task RecordFailure_IsCounted_WithoutAbandoningOtherWorkers()
    {
        using var h = new Harness { FailAt = 7 };
        await h.RunAsync(105);
        Assert.Equal(104, h.Written.Count);
        Assert.Equal(0, h.Active);
        Assert.Single(h.History, row => row.StepSubtypeSnapshot == "loop-batch" && row.Status == "Failed");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task StagedRecordFailure_IsSkipped_AndEveryOtherRecordIsStillProcessed(int workers)
    {
        // 1000 staged records, #205 (value "204") fails: the other 999 are written, 205 is marked failed-and-skipped (3),
        // nothing is left pending and the run completes instead of stopping or retrying forever.
        using var h = new Harness(concurrency: workers) { FailAt = 204 };
        await h.RunAsync(1000, staged: true);

        Assert.Equal(999, h.Written.Count);
        Assert.DoesNotContain("204", h.Written);
        Assert.Equal(999, h.Status.Count(kv => kv.Value == 1));
        Assert.Equal((byte)3, h.Status[205]);
        Assert.DoesNotContain(h.Status.Values, v => v is 0 or 2);
        Assert.Contains(h.History, row => row.StepSubtypeSnapshot == "loop-batch" && row.Status == "Failed");
    }

    [Fact]
    public async Task StagedInfrastructureFailure_StillStopsTheLoop_AndLeavesTheRecordRetryable()
    {
        using var h = new Harness(concurrency: 1) { FailAt = -1, InfrastructureFailure = new OperationCanceledException() };
        using var cancel = new CancellationTokenSource(60);
        h.DelayMs = 30;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(1000, staged: true, ct: cancel.Token));

        Assert.DoesNotContain(h.Status.Values, v => v == 3);
        Assert.Contains(h.Status.Values, v => v == 0);
    }

    [Fact]
    public async Task Cancellation_DrainsWorkersBeforeReturning()
    {
        using var h = new Harness { DelayMs = 100 };
        using var cancel = new CancellationTokenSource(80);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(1000, ct: cancel.Token));
        Assert.Equal(0, h.Active);
        Assert.True(h.Written.Count < 1000);
    }

    [Fact]
    public async Task Benchmark_SequentialVersusParallel_SameThousandRecords()
    {
        using var serial = new Harness(1);
        using var parallel = new Harness(4);
        var timer = Stopwatch.StartNew();
        await serial.RunAsync(1000);
        var sequentialMs = timer.ElapsedMilliseconds;
        timer.Restart();
        await parallel.RunAsync(1000);
        var parallelMs = timer.ElapsedMilliseconds;
        output.WriteLine($"Controlled 1000-record benchmark (mock DB, {serial.DelayMs}ms write): sequential={sequentialMs}ms; parallel={parallelMs}ms; speedup={(double)sequentialMs / parallelMs:F2}x.");
        Assert.Equal(serial.Written.OrderBy(v => v), parallel.Written.OrderBy(v => v));
        Assert.InRange(parallel.Peak, 2, 4);
    }
}
