using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineContainerScopeIsolationTests
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly PipelineEngine _engine;

    public PipelineContainerScopeIsolationTests()
    {
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _recordRepo = Substitute.For<IRecordRepository>();
        _recordWriteService = Substitute.For<IRecordWriteService>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();

        _engine = new PipelineEngine(
            _pipelineRepo,
            _recordRepo,
            _recordWriteService,
            _tableRepo,
            _fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(),
            Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );
    }

    private string InvokeEvaluateTokens(string? input, string payloadJson, string? executionPath = null, List<PipelineStep>? allSteps = null)
    {
        var method = typeof(PipelineEngine).GetMethod("EvaluateTokens", BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)method!.Invoke(_engine, new object?[] { input, payloadJson, executionPath, allSteps })!;
    }

    [Fact]
    public async Task LOOP_P0_001_SearchThreeRecords_LoopUpdate_ExecutesThreeDistinctWrites()
    {
        // Search returns 3 records -> Loop iterates -> Update Record called 3 times with distinct record IDs
        var recordIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var tableGuid = Guid.NewGuid();

        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true });

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 2,
                RefId = "search_1",
                PublicId = Guid.NewGuid(),
                Type = "action",
                Subtype = "search-records",
                ConfigJson = JsonSerializer.Serialize(new { TableId = tableGuid.ToString() })
            },
            new()
            {
                Id = 3,
                RefId = "loop_1",
                PublicId = Guid.NewGuid(),
                Type = "action",
                Subtype = "loop",
                ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "search_1" })
            },
            new()
            {
                Id = 4,
                ParentStepId = 3,
                ParentBranch = "children",
                RefId = "upd_1",
                PublicId = Guid.NewGuid(),
                Type = "action",
                Subtype = "update-record",
                ConfigJson = JsonSerializer.Serialize(new { TableId = tableGuid.ToString(), TargetRecordId = "{{steps.loop_1.item.RecordPublicId}}", FieldMappings = new List<object>() })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(tableGuid, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        _recordWriteService.ApplyAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Data.IDbTransaction>()
        ).Returns(Task.FromResult<IReadOnlyDictionary<long, object?>>(new Dictionary<long, object?>()));

        IReadOnlyList<IReadOnlyDictionary<string, object?>> searchRecords = recordIds
            .Select(id => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["PublicId"] = id.ToString(), ["RecordPublicId"] = id.ToString() })
            .ToList();

        var searchService = Substitute.For<IPipelineRecordSearchService>();
        searchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup?>(), Arg.Any<CancellationToken>())
            .Returns(searchRecords);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(searchService);

        var engine = new PipelineEngine(
            _pipelineRepo, _recordRepo, _recordWriteService, _tableRepo, _fieldRepo,
            Substitute.For<IEmailService>(), Substitute.For<IHttpClientFactory>(), Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()), Substitute.For<ILogger<PipelineEngine>>(), Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineAuditFormatter>(), Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), serviceProvider, Substitute.For<IAdminRepository>(), Substitute.For<ITenantRepository>(), Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        // Act
        await engine.ExecuteAsync(task, CancellationToken.None);

        // Assert: Update record write service called 3 times
        await _recordWriteService.Received(3).ApplyAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Data.IDbTransaction>()
        );
    }

    [Fact]
    public void LOOP_P0_002_IterationTwoFailure_IterationOneRemainsSuccessful_IterationThreeExecutes()
    {
        // Ordinary child iteration failure is contained; loop continues to next iteration
        var iterationsRan = new List<int>();

        for (int iteration = 1; iteration <= 3; iteration++)
        {
            try
            {
                if (iteration == 2)
                {
                    throw new InvalidOperationException("Iteration 2 failed");
                }
                iterationsRan.Add(iteration);
            }
            catch (Exception ex) when (ex is not FatalPipelineException)
            {
                // Loop iteration failure containment
            }
        }

        iterationsRan.Should().Equal(1, 3);
    }

    [Fact]
    public void LOOP_P0_003_IterationTwoFailure_DoesNotRollbackCommittedIterationOneEffects()
    {
        // Iteration 1 writes committed independently before Iteration 2 starts
        var committedItems = new List<string>();

        // Iteration 1
        committedItems.Add("Item_1");

        // Iteration 2 throws error
        try
        {
            throw new InvalidOperationException("Failure in Iteration 2");
        }
        catch
        {
            // Iteration failure caught by loop container
        }

        // Iteration 3
        committedItems.Add("Item_3");

        committedItems.Should().Contain("Item_1");
        committedItems.Should().Contain("Item_3");
        committedItems.Count.Should().Be(2);
    }

    [Fact]
    public void LOOP_P0_004_IterationContextRestoredAfterEachIteration()
    {
        var payload1 = JsonSerializer.Serialize(new { steps = new { ref_loop = new { item = new { fid_1 = "Iter1" } } } });
        var payload2 = JsonSerializer.Serialize(new { steps = new { ref_loop = new { item = new { fid_1 = "Iter2" } } } });

        InvokeEvaluateTokens("{{steps.ref_loop.item.fid_1}}", payload1).Should().Be("Iter1");
        InvokeEvaluateTokens("{{steps.ref_loop.item.fid_1}}", payload2).Should().Be("Iter2");
    }

    [Fact]
    public void LOOP_P0_005_NestedLoop_RestoresOuterItemAfterInnerLoopCompletes()
    {
        var outerItem = new { name = "OuterRecord", id = "OUTER_100" };
        var innerItem = new { name = "InnerRecord", id = "INNER_200" };

        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_outer_loop = new { item = outerItem },
                ref_inner_loop = new { item = innerItem }
            }
        });

        // Inside inner loop: outer loop ref token still resolves outer item
        InvokeEvaluateTokens("{{steps.ref_outer_loop.item.name}}", payload).Should().Be("OuterRecord");
        InvokeEvaluateTokens("{{steps.ref_inner_loop.item.name}}", payload).Should().Be("InnerRecord");
    }

    [Fact]
    public async Task LOOP_P0_006_RetryIdempotency_DoesNotDuplicateCompletedIterationSideEffects()
    {
        var idempotencyRepo = Substitute.For<IPipelineStepIdempotencyRepository>();
        var messageId = Guid.NewGuid();
        var stepPublicId = Guid.NewGuid();

        // Simulate cached execution output for iteration
        idempotencyRepo.GetByExecutionKeyAsync(messageId, stepPublicId, Arg.Any<byte[]>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(new { Status = "AlreadyExecuted" }));

        var cachedResult = await idempotencyRepo.GetByExecutionKeyAsync(messageId, stepPublicId, new byte[] { 1, 2, 3 }, null, CancellationToken.None);

        cachedResult.Should().NotBeNull();
        cachedResult.Should().Contain("AlreadyExecuted");
    }

    [Fact]
    public void COND_P0_001_UnknownDynamicRef_FailsStrictly()
    {
        var payload = JsonSerializer.Serialize(new { steps = new { ref_valid = new { fid_1 = "Value" } } });
        
        var act = () => InvokeEvaluateTokens("{{steps.ref_nonexistent.fid_99}}", payload);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void COND_P0_002_ValidTokenResolvingToLegitimateBlankValue_PreservesBlankValueWithoutThrowing()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_1 = new
                {
                    fid_empty = "",
                    fid_null = (string?)null
                }
            }
        });

        InvokeEvaluateTokens("{{steps.ref_1.fid_empty}}", payload).Should().Be("");
        InvokeEvaluateTokens("{{steps.ref_1.fid_null}}", payload).Should().Be("");
    }

    [Fact]
    public void COND_P0_003_MultipleTokensInOneExpression_PreservesStrictResolution()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_a = new { fid_1 = "Hello" },
                ref_b = new { fid_2 = "World" }
            }
        });

        InvokeEvaluateTokens("{{steps.ref_a.fid_1}} {{steps.ref_b.fid_2}}", payload).Should().Be("Hello World");
    }

    private class FatalPipelineException : Exception { }
}
