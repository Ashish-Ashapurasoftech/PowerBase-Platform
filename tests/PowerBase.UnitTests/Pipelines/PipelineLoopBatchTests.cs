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
                            if (mapped == FailAt.ToString()) throw FailWith ?? new InvalidOperationException("Injected record failure");
                            Written.Add(mapped);
                            return Guid.NewGuid();
                        }
                        finally { Interlocked.Decrement(ref Active); }
                    });
                records.HasValueDuplicateAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<object>(), Arg.Any<long?>(), Arg.Any<IDbTransaction?>(), Arg.Any<CancellationToken>())
                    .Returns(call => Task.FromResult(ExistingValues.Contains(call.ArgAt<object>(2).ToString()!)));
                return records;
            });
            var tables = Substitute.For<IAppTableRepository>();
            tables.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 1, PublicId = TableId });
            services.AddSingleton(tables);
            var fields = Substitute.For<IAppFieldRepository>();
            fields.ListByTableAsync(1, Arg.Any<CancellationToken>()).Returns(_ => new List<AppField> {
                new() { Id = 6, Fid = 6, Name = "name", TypeCode = "text", IsRequired = FieldRequired, IsUnique = FieldUnique, DefaultValue = FieldDefault }
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
        public Exception? FailWith;
        // The mapped field's constraints, and the values that already exist in the destination table (for the Unique check).
        public bool FieldRequired, FieldUnique;
        public string? FieldDefault;
        public readonly HashSet<string> ExistingValues = new();

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

    /// <summary>Stands in for a SqlException: the engine reads its <c>Number</c> by reflection.</summary>
    private sealed class FakeSqlError(int number, Exception? inner = null) : Exception("Simulated SQL error " + number, inner)
    {
        public int Number { get; } = number;
    }

    [Theory]
    [InlineData(233, false)]    // "A transport-level error has occurred": the connection dropped mid-transaction
    [InlineData(10054, false)]  // connection reset by the server
    [InlineData(40613, false)]  // Azure SQL database unavailable (failover)
    [InlineData(233, true)]     // the same error wrapped by another exception
    [InlineData(1205, false)]   // deadlock victim, as before
    public async Task StagedLostConnection_LeavesTheRecordPendingForARetry_InsteadOfSkippingIt(int number, bool wrapped)
    {
        // A record whose write died with a broken connection (or a deadlock) did nothing wrong: it must stay pending so the
        // retry writes it. Recording it as failed-and-skipped (3) would lose the record for good.
        var error = wrapped ? new InvalidOperationException("Create Record failed", new FakeSqlError(number)) : (Exception)new FakeSqlError(number);
        using var h = new Harness(concurrency: 1) { FailAt = 204, FailWith = error };

        await Assert.ThrowsAnyAsync<Exception>(() => h.RunAsync(1000, staged: true));

        Assert.DoesNotContain(h.Status.Values, v => v == 3);
        Assert.Equal((byte)0, h.Status[205]);
        Assert.DoesNotContain("204", h.Written);
    }

    [Fact]
    public async Task StagedOrdinarySqlError_IsStillTheRecordsOwnFailure_AndIsSkipped()
    {
        // 547 (a foreign-key violation) is about this record's data, not about the connection: skipped, the others continue.
        using var h = new Harness(concurrency: 1) { FailAt = 204, FailWith = new FakeSqlError(547) };

        await h.RunAsync(1000, staged: true);

        Assert.Equal((byte)3, h.Status[205]);
        Assert.Equal(999, h.Written.Count);
    }

    // ── Create Record applies the field rules (Required / Unique), like adding the record by hand ─────────────────────────

    [Fact]
    public async Task CreateRecord_UniqueField_RejectsADuplicateValue_AndWritesTheOthers()
    {
        using var h = new Harness(concurrency: 1) { FieldUnique = true };
        h.ExistingValues.Add("204");                 // the record with value 204 is already in the table

        await h.RunAsync(1000, staged: true);

        Assert.DoesNotContain("204", h.Written);
        Assert.Equal(999, h.Written.Count);
        Assert.Equal((byte)3, h.Status[205]);        // rejected: failed and skipped, the loop went on
        Assert.Equal(999, h.Status.Count(kv => kv.Value == 1));
        Assert.Contains(h.Errors, error => error.Contains("must be unique"));
    }

    [Fact]
    public async Task CreateRecord_FieldWithoutUnique_StillAcceptsTheSameValue()
    {
        using var h = new Harness(concurrency: 1) { FieldUnique = false };
        h.ExistingValues.Add("204");

        await h.RunAsync(300, staged: true);

        Assert.Equal(300, h.Written.Count);
        Assert.Contains("204", h.Written);
    }

    [Fact]
    public async Task CreateRecord_RequiredFieldLeftBlank_IsRejectedAndNothingIsWritten()
    {
        using var h = new Harness(concurrency: 1) { FieldRequired = true };

        await h.RunAsync(20, mapping: "", staged: true);   // the mapping resolves to blank, so the field is not written

        Assert.Empty(h.Written);
        Assert.All(h.Status.Values, status => Assert.Equal((byte)3, status));
        Assert.Contains(h.Errors, error => error.Contains("is required"));
    }

    [Fact]
    public async Task CreateRecord_RequiredFieldWithADefault_GetsTheDefault()
    {
        using var h = new Harness(concurrency: 1) { FieldRequired = true, FieldDefault = "fallback" };

        await h.RunAsync(5, mapping: "", staged: true);

        Assert.Equal(5, h.Written.Count);
        Assert.All(h.Written, value => Assert.Equal("fallback", value));
    }

    [Fact]
    public async Task CreateRecord_RequiredFieldWithAValue_IsWritten()
    {
        using var h = new Harness(concurrency: 1) { FieldRequired = true };

        await h.RunAsync(50, staged: true);

        Assert.Equal(50, h.Written.Count);
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
