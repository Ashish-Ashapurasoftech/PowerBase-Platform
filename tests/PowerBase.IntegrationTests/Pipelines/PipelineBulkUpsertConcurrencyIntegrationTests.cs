using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.UOW;
using Xunit;
using Xunit.Abstractions;

namespace PowerBase.IntegrationTests.Pipelines;

public class PipelineBulkUpsertConcurrencyIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private const string TenantConnectionString = "Server=DESKTOP-FKLGO88;Database=Powerbase_1;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";

    public PipelineBulkUpsertConcurrencyIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private class TestTenantConnectionFactory : ITenantConnectionFactory
    {
        private readonly string _connectionString;
        public TestTenantConnectionFactory(string connectionString) => _connectionString = connectionString;
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new SqlConnection(_connectionString));
        }
    }

    private IQueryContext CreateQueryContext()
    {
        var qc = Substitute.For<IQueryContext>();
        qc.TenantId.Returns(1L);
        qc.UserId.Returns(1L);
        qc.TenantRole.Returns("Admin");
        qc.IsTenantAdmin.Returns(true);
        qc.IsSuperAdmin.Returns(true);
        qc.Permissions.Returns(new HashSet<string>());
        qc.AllowedAppIds.Returns(new HashSet<long>());
        return qc;
    }

    private (PipelineEngine Engine, AppTable Table, List<AppField> Fields, IRecordRepository RecordRepo, ITenantUnitOfWork Uow) CreateEngineAndTable(
        ITenantConnectionFactory connFactory, IQueryContext queryContext)
    {
        var encryptionService = Substitute.For<IEncryptionService>();
        var messagePublisher = Substitute.For<IMessagePublisher>();
        var controlConnFactory = Substitute.For<IControlConnectionFactory>();

        var recordRepo = new RecordRepository(connFactory, queryContext, messagePublisher, encryptionService, controlConnFactory);
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();

        var recordWriteService = Substitute.For<IRecordWriteService>();
        recordWriteService.ApplyAsync(
            Arg.Any<AppTable>(),
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<bool>(),
            Arg.Any<Action<SearchIndexMessage>?>()
        ).Returns(async callInfo =>
        {
            var tbl = callInfo.Arg<AppTable>();
            var flds = callInfo.Arg<IReadOnlyList<AppField>>();
            var pubId = callInfo.Arg<Guid>();
            var vals = callInfo.Arg<IReadOnlyDictionary<long, object?>>();
            var tx = callInfo.Arg<IDbTransaction?>();
            var ct = callInfo.Arg<CancellationToken>();
            await recordRepo.UpdateAsync(tbl, flds, pubId, vals, tx, ct);
            return vals;
        });

        var triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
        var idempotencyRepo = Substitute.For<IPipelineStepIdempotencyRepository>();
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var recordSearchService = Substitute.For<IPipelineRecordSearchService>();
        var uow = new TenantUnitOfWork(connFactory);

        var execOptions = Options.Create(new PipelineExecutionOptions());
        var logger = NullLogger<PipelineEngine>.Instance;
        var auditFormatter = Substitute.For<IPipelineAuditFormatter>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        var engine = new PipelineEngine(
            pipelineRepo,
            recordRepo,
            recordWriteService,
            tableRepo,
            fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<System.Net.Http.IHttpClientFactory>(),
            fileStorage,
            execOptions,
            logger,
            triggerInterceptor,
            uow,
            auditFormatter,
            queryContext,
            Substitute.For<IServiceScopeFactory>(),
            serviceProvider,
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            idempotencyRepo
        );

        var table = new AppTable
        {
            Id = 1,
            AppId = 1,
            PublicId = Guid.Parse("2f1f633d-0fac-f111-b209-1c1b0d811b4d"),
            Name = "Hotels"
        };

        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 3, Name = "S_recordId", TypeCode = "Number", PhysicalColumnName = "Id", IsSystem = true },
            new() { Id = 2, Fid = 1, Name = "S_dateCreated", TypeCode = "DateTime", PhysicalColumnName = "CreatedOn", IsSystem = true },
            new() { Id = 3, Fid = 2, Name = "S_dateModified", TypeCode = "DateTime", PhysicalColumnName = "ModifiedOn", IsSystem = true },
            new() { Id = 4, Fid = 4, Name = "S_recordOwner", TypeCode = "User", PhysicalColumnName = "CreatedBy", IsSystem = true },
            new() { Id = 5, Fid = 5, Name = "S_lastModifiedBy", TypeCode = "User", PhysicalColumnName = "ModifiedBy", IsSystem = true },
            new() { Id = 6, Fid = 6, Name = "C_hotelName", TypeCode = "Text", PhysicalColumnName = "f_6" }
        };

        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        return (engine, table, fields, recordRepo, uow);
    }

    private async Task<string> RunStepAsync(
        PipelineEngine engine,
        PipelineStep step,
        Dictionary<string, object> contextDict,
        IRecordRepository recordRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordWriteService recordWriteService,
        IPipelineTriggerInterceptor triggerInterceptor,
        ITenantUnitOfWork uow,
        IPipelineStepIdempotencyRepository idempotencyRepo)
    {
        var fileStorage = Substitute.For<IFileStorageService>();
        var recordSearchService = Substitute.For<IPipelineRecordSearchService>();

        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var task = (Task<string>)method!.Invoke(engine, new object[] {
            step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), $"step_concurrency_{Guid.NewGuid():N}",
            recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo, fileStorage, recordSearchService, CancellationToken.None
        })!;

        return await task;
    }

    [Fact]
    public async Task LiveSqlServer_ConcurrentSameMergeKey_PreventsDuplicatesAndEnforcesIsolation()
    {
        // Check live connection
        await using (var testConn = new SqlConnection(TenantConnectionString))
        {
            try { await testConn.OpenAsync(); }
            catch (Exception ex)
            {
                _output.WriteLine($"SQL Server not reachable: {ex.Message}");
                return;
            }
        }

        var connFactory = new TestTenantConnectionFactory(TenantConnectionString);
        var queryContext = CreateQueryContext();
        var (_, table, fields, _, _) = CreateEngineAndTable(connFactory, queryContext);

        string sharedMergeKey = $"ConcurrentHotel_{Guid.NewGuid():N}";
        int threadCount = 2;
        var startBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = new ConcurrentBag<(int ThreadId, bool Success, string? OutputJson, Exception? Error)>();

        var tasks = Enumerable.Range(1, threadCount).Select(async threadId =>
        {
            try
            {
                var threadConnFactory = new TestTenantConnectionFactory(TenantConnectionString);
                var threadContext = CreateQueryContext();
                var (threadEngine, _, _, recordRepo, uow) = CreateEngineAndTable(threadConnFactory, threadContext);

                var tableRepo = Substitute.For<IAppTableRepository>();
                tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
                var fieldRepo = Substitute.For<IAppFieldRepository>();
                fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

                var recordWriteService = Substitute.For<IRecordWriteService>();
                recordWriteService.ApplyAsync(
                    Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>(), Arg.Any<bool>(), Arg.Any<Action<SearchIndexMessage>?>()
                ).Returns(async callInfo =>
                {
                    var tbl = callInfo.Arg<AppTable>();
                    var flds = callInfo.Arg<IReadOnlyList<AppField>>();
                    var pubId = callInfo.Arg<Guid>();
                    var vals = callInfo.Arg<IReadOnlyDictionary<long, object?>>();
                    var tx = callInfo.Arg<IDbTransaction?>();
                    var ct = callInfo.Arg<CancellationToken>();
                    await recordRepo.UpdateAsync(tbl, flds, pubId, vals, tx, ct);
                    return vals;
                });

                var triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
                var idempotencyRepo = Substitute.For<IPipelineStepIdempotencyRepository>();

                var contextDict = new Dictionary<string, object> { ["_CreatedBy"] = (long)threadId };

                var prepStep = new PipelineStep
                {
                    RefId = $"prep_{threadId}",
                    Type = "action",
                    Subtype = "prepare-bulk-upsert",
                    ConfigJson = JsonSerializer.Serialize(new
                    {
                        tableLabel = table.PublicId.ToString(),
                        mergeKeyFid = "fid_6"
                    })
                };
                await RunStepAsync(threadEngine, prepStep, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);

                var addRow = new PipelineStep
                {
                    RefId = $"row_{threadId}",
                    Type = "action",
                    Subtype = "add-bulk-upsert-row",
                    ConfigJson = JsonSerializer.Serialize(new
                    {
                        parentUpsertStepRefId = $"prep_{threadId}",
                        rowValues = new Dictionary<string, object?>
                        {
                            ["fid_6"] = sharedMergeKey
                        }
                    })
                };
                await RunStepAsync(threadEngine, addRow, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);

                var commitStep = new PipelineStep
                {
                    RefId = $"commit_{threadId}",
                    Type = "action",
                    Subtype = "commit-upsert",
                    ConfigJson = JsonSerializer.Serialize(new
                    {
                        parentUpsertStepRefId = $"prep_{threadId}"
                    })
                };

                // Synchronize launch
                await startBarrier.Task;

                var outputJson = await RunStepAsync(threadEngine, commitStep, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);
                results.Add((threadId, true, outputJson, null));
            }
            catch (Exception ex)
            {
                results.Add((threadId, false, null, ex));
            }
        }).ToList();

        // Release all threads simultaneously
        startBarrier.SetResult(true);
        await Task.WhenAll(tasks);

        // Verify in database: exactly 1 record with this merge key exists
        await using var verifyConn = new SqlConnection(TenantConnectionString);
        await verifyConn.OpenAsync();
        var recordCount = await verifyConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM data.t_1 WHERE IsDeleted = 0 AND f_6 = @key", new { key = sharedMergeKey });

        _output.WriteLine($"[Live SQL Server] Concurrent Same Merge Key Results:");
        _output.WriteLine($"- Shared Merge Key: {sharedMergeKey}");
        _output.WriteLine($"- Database Records Created: {recordCount}");
        foreach (var r in results)
        {
            _output.WriteLine($"  Thread {r.ThreadId}: Success={r.Success}, Output={r.OutputJson}, Error={r.Error?.Message}");
        }

        // Cleanup test record
        await verifyConn.ExecuteAsync("DELETE FROM data.t_1 WHERE f_6 = @key", new { key = sharedMergeKey });

        // Assertions: Zero duplicates created
        recordCount.Should().Be(1, "Exactly 1 record must exist in SQL Server for the shared merge key (no duplicates)");
    }

    [Fact]
    public async Task LiveSqlServer_ConcurrentDifferentMergeKeys_BothSucceedWithoutBlocking()
    {
        await using (var testConn = new SqlConnection(TenantConnectionString))
        {
            try { await testConn.OpenAsync(); }
            catch { return; }
        }

        var connFactory = new TestTenantConnectionFactory(TenantConnectionString);
        var queryContext = CreateQueryContext();
        var (_, table, fields, _, _) = CreateEngineAndTable(connFactory, queryContext);

        int threadCount = 4;
        var mergeKeys = Enumerable.Range(1, threadCount).Select(i => $"DiffHotel_{i}_{Guid.NewGuid():N}").ToList();
        var startBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new ConcurrentBag<(int ThreadId, string Key, bool Success, string? OutputJson, Exception? Error)>();

        var tasks = Enumerable.Range(0, threadCount).Select(async i =>
        {
            var key = mergeKeys[i];
            int threadId = i + 1;
            try
            {
                var threadConnFactory = new TestTenantConnectionFactory(TenantConnectionString);
                var threadContext = CreateQueryContext();
                var (threadEngine, _, _, recordRepo, uow) = CreateEngineAndTable(threadConnFactory, threadContext);

                var tableRepo = Substitute.For<IAppTableRepository>();
                tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
                var fieldRepo = Substitute.For<IAppFieldRepository>();
                fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

                var recordWriteService = Substitute.For<IRecordWriteService>();
                var triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
                var idempotencyRepo = Substitute.For<IPipelineStepIdempotencyRepository>();

                var contextDict = new Dictionary<string, object> { ["_CreatedBy"] = (long)threadId };

                var prepStep = new PipelineStep
                {
                    RefId = $"prep_diff_{threadId}",
                    Type = "action",
                    Subtype = "prepare-bulk-upsert",
                    ConfigJson = JsonSerializer.Serialize(new
                    {
                        tableLabel = table.PublicId.ToString(),
                        mergeKeyFid = "fid_6"
                    })
                };
                await RunStepAsync(threadEngine, prepStep, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);

                var addRow = new PipelineStep
                {
                    RefId = $"row_diff_{threadId}",
                    Type = "action",
                    Subtype = "add-bulk-upsert-row",
                    ConfigJson = JsonSerializer.Serialize(new
                    {
                        parentUpsertStepRefId = $"prep_diff_{threadId}",
                        rowValues = new Dictionary<string, object?> { ["fid_6"] = key }
                    })
                };
                await RunStepAsync(threadEngine, addRow, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);

                var commitStep = new PipelineStep
                {
                    RefId = $"commit_diff_{threadId}",
                    Type = "action",
                    Subtype = "commit-upsert",
                    ConfigJson = JsonSerializer.Serialize(new
                    {
                        parentUpsertStepRefId = $"prep_diff_{threadId}"
                    })
                };

                await startBarrier.Task;

                var outputJson = await RunStepAsync(threadEngine, commitStep, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);
                results.Add((threadId, key, true, outputJson, null));
            }
            catch (Exception ex)
            {
                results.Add((threadId, key, false, null, ex));
            }
        }).ToList();

        startBarrier.SetResult(true);
        await Task.WhenAll(tasks);

        await using var verifyConn = new SqlConnection(TenantConnectionString);
        await verifyConn.OpenAsync();
        var recordCount = await verifyConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM data.t_1 WHERE IsDeleted = 0 AND f_6 IN @keys", new { keys = mergeKeys });

        _output.WriteLine($"[Live SQL Server] Concurrent Different Merge Keys Results:");
        _output.WriteLine($"- Total Keys: {threadCount}, Inserted in DB: {recordCount}");
        foreach (var r in results)
        {
            _output.WriteLine($"  Thread {r.ThreadId} ({r.Key}): Success={r.Success}, Output={r.OutputJson}, Error={r.Error?.Message}");
        }

        // Cleanup
        await verifyConn.ExecuteAsync("DELETE FROM data.t_1 WHERE f_6 IN @keys", new { keys = mergeKeys });

        recordCount.Should().Be(threadCount, "All concurrent non-conflicting merge keys must be created successfully");
        results.All(r => r.Success).Should().BeTrue();
    }

    [Fact]
    public async Task LiveSqlServer_StressConcurrency_MultipleRounds_ZeroDuplicatesAndZeroDeadlocks()
    {
        await using (var testConn = new SqlConnection(TenantConnectionString))
        {
            try { await testConn.OpenAsync(); }
            catch { return; }
        }

        var connFactory = new TestTenantConnectionFactory(TenantConnectionString);
        var queryContext = CreateQueryContext();
        var (_, table, fields, _, _) = CreateEngineAndTable(connFactory, queryContext);

        int rounds = 5;
        int threadsPerRound = 4;
        int totalInserted = 0;
        int totalUpdated = 0;
        int deadlockCount = 0;
        int uniqueViolationCount = 0;
        int errorCount = 0;
        var allCreatedKeys = new List<string>();

        var sw = Stopwatch.StartNew();

        for (int round = 1; round <= rounds; round++)
        {
            string roundKeyA = $"Stress_A_{round}_{Guid.NewGuid():N}";
            string roundKeyB = $"Stress_B_{round}_{Guid.NewGuid():N}";
            allCreatedKeys.Add(roundKeyA);
            allCreatedKeys.Add(roundKeyB);

            // 2 threads target Key A, 2 threads target Key B
            var keysForThreads = new[] { roundKeyA, roundKeyA, roundKeyB, roundKeyB };
            var startBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var roundTasks = Enumerable.Range(0, threadsPerRound).Select(async i =>
            {
                var key = keysForThreads[i];
                int threadId = i + 1;
                try
                {
                    var threadConnFactory = new TestTenantConnectionFactory(TenantConnectionString);
                    var threadContext = CreateQueryContext();
                    var (threadEngine, _, _, recordRepo, uow) = CreateEngineAndTable(threadConnFactory, threadContext);

                    var tableRepo = Substitute.For<IAppTableRepository>();
                    tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
                    var fieldRepo = Substitute.For<IAppFieldRepository>();
                    fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

                    var recordWriteService = Substitute.For<IRecordWriteService>();
                    recordWriteService.ApplyAsync(
                        Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(),
                        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<IDbTransaction?>(), Arg.Any<bool>(), Arg.Any<Action<SearchIndexMessage>?>()
                    ).Returns(async callInfo =>
                    {
                        var tbl = callInfo.Arg<AppTable>();
                        var flds = callInfo.Arg<IReadOnlyList<AppField>>();
                        var pubId = callInfo.Arg<Guid>();
                        var vals = callInfo.Arg<IReadOnlyDictionary<long, object?>>();
                        var tx = callInfo.Arg<IDbTransaction?>();
                        var ct = callInfo.Arg<CancellationToken>();
                        await recordRepo.UpdateAsync(tbl, flds, pubId, vals, tx, ct);
                        return vals;
                    });

                    var triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
                    var idempotencyRepo = Substitute.For<IPipelineStepIdempotencyRepository>();

                    var contextDict = new Dictionary<string, object> { ["_CreatedBy"] = (long)(round * 10 + threadId) };

                    var prepStep = new PipelineStep
                    {
                        RefId = $"prep_{round}_{threadId}",
                        Type = "action",
                        Subtype = "prepare-bulk-upsert",
                        ConfigJson = JsonSerializer.Serialize(new
                        {
                            tableLabel = table.PublicId.ToString(),
                            mergeKeyFid = "fid_6"
                        })
                    };
                    await RunStepAsync(threadEngine, prepStep, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);

                    var addRow = new PipelineStep
                    {
                        RefId = $"row_{round}_{threadId}",
                        Type = "action",
                        Subtype = "add-bulk-upsert-row",
                        ConfigJson = JsonSerializer.Serialize(new
                        {
                            parentUpsertStepRefId = $"prep_{round}_{threadId}",
                            rowValues = new Dictionary<string, object?> { ["fid_6"] = key }
                        })
                    };
                    await RunStepAsync(threadEngine, addRow, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);

                    var commitStep = new PipelineStep
                    {
                        RefId = $"commit_{round}_{threadId}",
                        Type = "action",
                        Subtype = "commit-upsert",
                        ConfigJson = JsonSerializer.Serialize(new
                        {
                            parentUpsertStepRefId = $"prep_{round}_{threadId}"
                        })
                    };

                    await startBarrier.Task;

                    var outputJson = await RunStepAsync(threadEngine, commitStep, contextDict, recordRepo, tableRepo, fieldRepo, recordWriteService, triggerInterceptor, uow, idempotencyRepo);
                    var doc = JsonSerializer.Deserialize<JsonElement>(outputJson);
                    Interlocked.Add(ref totalInserted, doc.GetProperty("inserted_count").GetInt32());
                    Interlocked.Add(ref totalUpdated, doc.GetProperty("updated_count").GetInt32());
                }
                catch (SqlException sqlEx) when (sqlEx.Number == 1205)
                {
                    Interlocked.Increment(ref deadlockCount);
                }
                catch (SqlException sqlEx) when (sqlEx.Number == 2627 || sqlEx.Number == 2601)
                {
                    Interlocked.Increment(ref uniqueViolationCount);
                }
                catch
                {
                    Interlocked.Increment(ref errorCount);
                }
            }).ToList();

            startBarrier.SetResult(true);
            await Task.WhenAll(roundTasks);
        }

        sw.Stop();

        // Verify database counts
        await using var verifyConn = new SqlConnection(TenantConnectionString);
        await verifyConn.OpenAsync();
        var dbCount = await verifyConn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM data.t_1 WHERE IsDeleted = 0 AND f_6 IN @keys", new { keys = allCreatedKeys });

        var duplicateCount = await verifyConn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(1) FROM (
                SELECT f_6, COUNT(1) as cnt
                FROM data.t_1
                WHERE IsDeleted = 0 AND f_6 IN @keys
                GROUP BY f_6
                HAVING COUNT(1) > 1
            ) dupes", new { keys = allCreatedKeys });

        _output.WriteLine($"[Live SQL Server Stress Concurrency Metrics]");
        _output.WriteLine($"- Environment: Microsoft SQL Server 2022 Developer (Database: Powerbase_1)");
        _output.WriteLine($"- Total Rounds: {rounds}, Threads per Round: {threadsPerRound}, Total Executions: {rounds * threadsPerRound}");
        _output.WriteLine($"- Total Duration: {sw.ElapsedMilliseconds} ms");
        _output.WriteLine($"- Total Records in DB: {dbCount} (Expected: {allCreatedKeys.Count})");
        _output.WriteLine($"- Duplicate Count: {duplicateCount}");
        _output.WriteLine($"- Inserted Count Reported: {totalInserted}");
        _output.WriteLine($"- Updated Count Reported: {totalUpdated}");
        _output.WriteLine($"- Deadlocks (SQL 1205): {deadlockCount}");
        _output.WriteLine($"- Unique Constraint Violations: {uniqueViolationCount}");
        _output.WriteLine($"- General Errors: {errorCount}");

        // Cleanup
        await verifyConn.ExecuteAsync("DELETE FROM data.t_1 WHERE f_6 IN @keys", new { keys = allCreatedKeys });

        // Assertions
        duplicateCount.Should().Be(0, "There must be ZERO duplicate records in SQL Server");
        dbCount.Should().Be(allCreatedKeys.Count, "Each unique merge key must have exactly 1 record in SQL Server");
        deadlockCount.Should().Be(0, "UPDLOCK/HOLDLOCK must prevent SQL Server deadlocks during concurrency");
    }
}
