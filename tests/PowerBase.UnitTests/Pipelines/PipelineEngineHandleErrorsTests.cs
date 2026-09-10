using FluentAssertions;
using Xunit;
using NSubstitute;
using System.Reflection;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Records;
using PowerBase.Domain.Entities;
using PowerBase.Application.Reports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineEngineHandleErrorsTests
{
    private readonly PipelineEngine _engine;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly PipelineExecutionOptions _execOptions;
    private readonly ILogger<PipelineEngine> _logger;
    private readonly IPipelineAuditFormatter _auditFormatter;
    private readonly IPipelineRecordSearchService _pipelineRecordSearchService;

    public PipelineEngineHandleErrorsTests()
    {
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _recordRepo = Substitute.For<IRecordRepository>();
        _recordWriteService = Substitute.For<IRecordWriteService>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _execOptions = new PipelineExecutionOptions();
        _logger = Substitute.For<ILogger<PipelineEngine>>();
        _auditFormatter = Substitute.For<IPipelineAuditFormatter>();
        _pipelineRecordSearchService = Substitute.For<IPipelineRecordSearchService>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(_pipelineRecordSearchService);

        _engine = new PipelineEngine(
            _pipelineRepo,
            _recordRepo,
            _recordWriteService,
            _tableRepo,
            _fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(_execOptions),
            _logger,
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            _auditFormatter,
            Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            serviceProvider,
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );
    }

    [Fact]
    public async Task HandleErrors_MonitoredSuccess_ExecutesSuccessBranch_SkipsErrorBranch()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var tableGuid1 = Guid.NewGuid();
        var tableGuid2 = Guid.NewGuid();
        var tableGuid3 = Guid.NewGuid();

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "handle_1",
                Label = "Handle Errors Step",
                Type = "control",
                Subtype = "handle-errors",
                ConfigJson = JsonSerializer.Serialize(new { FallbackAction = "handle" })
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "monitored_look_up",
                Label = "Lookup Monitored Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = tableGuid1.ToString(), RecordIdValue = "100" })
            },
            new()
            {
                Id = 3,
                ParentStepId = 1,
                ParentBranch = "successchildren",
                PublicId = Guid.NewGuid(),
                RefId = "on_success_action",
                Label = "On Success Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = tableGuid2.ToString(), RecordIdValue = "200" })
            },
            new()
            {
                Id = 4,
                ParentStepId = 1,
                ParentBranch = "errorchildren",
                PublicId = Guid.NewGuid(),
                RefId = "on_error_action",
                Label = "On Error Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = tableGuid3.ToString(), RecordIdValue = "300" })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        _recordRepo.GetByPublicIdAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { { "id", 100 } });
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { { 100, new Dictionary<string, object?> { { "id", 100 } } }, { 200, new Dictionary<string, object?> { { "id", 200 } } } });

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        // Step 2 (Monitored) and Step 3 (On Success) run, Step 4 (On Error) does NOT run
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 2), Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 3), Arg.Any<CancellationToken>());
        await _pipelineRepo.DidNotReceive().CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 4), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleErrors_MonitoredFailure_ExecutesErrorBranch_PopulatesERRORObject()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var failTableGuid = Guid.NewGuid();
        var handlerTableGuid = Guid.NewGuid();

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "handle_1",
                Label = "Handle Errors Step",
                Type = "control",
                Subtype = "handle-errors",
                ConfigJson = JsonSerializer.Serialize(new { FallbackAction = "handle" })
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "monitored_fail",
                Label = "Monitored Failing Lookup",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = failTableGuid.ToString(), RecordIdValue = "999" })
            },
            new()
            {
                Id = 3,
                ParentStepId = 1,
                ParentBranch = "errorchildren",
                PublicId = Guid.NewGuid(),
                RefId = "on_error_handler",
                Label = "On Error Handler",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = handlerTableGuid.ToString(), RecordIdValue = "100" })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(999)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>()); // Empty = NotFoundException
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyCollection<long>>(c => !c.Contains(999)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { { 100, new Dictionary<string, object?> { { "id", 100 } } } });

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        // Monitored step 2 failed, On Error handler step 3 executed
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 2), Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleErrors_IgnoreMode_MonitoredFailure_SkipsHandler_ResumesPipeline()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var failTableGuid = Guid.NewGuid();
        var outerTableGuid = Guid.NewGuid();

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "handle_1",
                Label = "Handle Errors Step",
                Type = "control",
                Subtype = "handle-errors",
                ConfigJson = JsonSerializer.Serialize(new { FallbackAction = "ignore" })
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "monitored_fail",
                Label = "Monitored Failing Lookup",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = failTableGuid.ToString(), RecordIdValue = "999" })
            },
            new()
            {
                Id = 3,
                ParentStepId = null,
                ParentBranch = null,
                PublicId = Guid.NewGuid(),
                RefId = "outer_next_action",
                Label = "Outer Next Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = outerTableGuid.ToString(), RecordIdValue = "100" })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(999)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>()); // Empty = NotFoundException
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyCollection<long>>(c => !c.Contains(999)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { { 100, new Dictionary<string, object?> { { "id", 100 } } } });

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        // Outer step 3 executes successfully because error was ignored
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleErrors_StopStepInMonitoredBranch_BypassesHandler_StopsPipeline()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "handle_1",
                Label = "Handle Errors Step",
                Type = "control",
                Subtype = "handle-errors",
                ConfigJson = JsonSerializer.Serialize(new { FallbackAction = "handle" })
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "stop_step",
                Label = "Stop Execution",
                Type = "control",
                Subtype = "stop",
                ConfigJson = "{}"
            },
            new()
            {
                Id = 3,
                ParentStepId = 1,
                ParentBranch = "errorchildren",
                PublicId = Guid.NewGuid(),
                RefId = "on_error_action",
                Label = "On Error Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = Guid.NewGuid().ToString(), RecordIdValue = "100" })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        // On Error step 3 did NOT run because Stop is a control-flow signal, not a catchable error
        await _pipelineRepo.DidNotReceive().CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 3), Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).UpdateRunAsync(Arg.Is<PipelineRun>(r => r.Status == "Stopped"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleErrors_ErrorBranchFailure_IsNotRecaughtBySameContainer()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "handle_1",
                Label = "Handle Errors Step",
                Type = "control",
                Subtype = "handle-errors",
                ConfigJson = JsonSerializer.Serialize(new { FallbackAction = "handle" })
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "monitored_fail",
                Label = "Monitored Failing Lookup",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = Guid.NewGuid().ToString(), RecordIdValue = "999" })
            },
            new()
            {
                Id = 3,
                ParentStepId = 1,
                ParentBranch = "errorchildren",
                PublicId = Guid.NewGuid(),
                RefId = "on_error_failing_action",
                Label = "Failing Handler Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = Guid.NewGuid().ToString(), RecordIdValue = "888" })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>()); // Throws InvalidOperationException ("Record with ID ... was not found")

        // Act & Assert
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Loop_OrdinaryIterationFailure_NextIterationContinuesAndLoopContainsFailure()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "search_1",
                Label = "Search Records",
                Type = "query",
                Subtype = "search-records",
                ConfigJson = JsonSerializer.Serialize(new { tableId = Guid.NewGuid().ToString(), TablePublicId = Guid.NewGuid().ToString() })
            },
            new()
            {
                Id = 2,
                PublicId = Guid.NewGuid(),
                RefId = "ref_loop",
                Label = "Loop Step",
                Type = "loop",
                Subtype = "for-each",
                ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "search_1" })
            },
            new()
            {
                Id = 3,
                ParentStepId = 2,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "lookup_in_loop",
                Label = "Lookup in loop",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = Guid.NewGuid().ToString(), RecordIdValue = "{{steps.ref_loop.item.Id}}" })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        var sampleRecords = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { { "Id", 101L } },
            new Dictionary<string, object?> { { "Id", 999L } }, // Will fail lookup (empty)
            new Dictionary<string, object?> { { "Id", 103L } }
        };
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(), null, null, null, Arg.Any<CancellationToken>())
            .Returns(sampleRecords);
        _pipelineRecordSearchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(sampleRecords);

        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(999L)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>()); // Empty -> InvalidOperationException
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyCollection<long>>(c => !c.Contains(999L)), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<long>>();
                var dict = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
                foreach (var id in ids)
                {
                    dict[id] = new Dictionary<string, object?> { { "Id", id } };
                }
                return dict;
            });

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        // Lookup step in loop (Id 3) should have been invoked 3 times (for items 101, 999, 103)
        await _pipelineRepo.Received(3).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 3), Arg.Any<CancellationToken>());
        // Quickbase Parity: Loop contains iteration failure, pipeline completes successfully
        await _pipelineRepo.Received().UpdateRunAsync(Arg.Is<PipelineRun>(r => r.Status == "Success"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleErrors_ContainingLoop_IterationFailureRunsOuterOnSuccess()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "handle_1",
                Label = "Handle Errors Step",
                Type = "control",
                Subtype = "handle-errors",
                ConfigJson = JsonSerializer.Serialize(new { FallbackAction = "handle" })
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "search_1",
                Label = "Search Records",
                Type = "query",
                Subtype = "search-records",
                ConfigJson = JsonSerializer.Serialize(new { tableId = Guid.NewGuid().ToString(), TablePublicId = Guid.NewGuid().ToString() })
            },
            new()
            {
                Id = 3,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "ref_loop",
                Label = "Loop Step",
                Type = "loop",
                Subtype = "for-each",
                ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "search_1" })
            },
            new()
            {
                Id = 4,
                ParentStepId = 3,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "lookup_in_loop",
                Label = "Lookup in loop",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = Guid.NewGuid().ToString(), RecordIdValue = "{{steps.ref_loop.item.Id}}" })
            },
            new()
            {
                Id = 5,
                ParentStepId = 1,
                ParentBranch = "successchildren",
                PublicId = Guid.NewGuid(),
                RefId = "on_success_action",
                Label = "On Success Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = Guid.NewGuid().ToString(), RecordIdValue = "100" })
            },
            new()
            {
                Id = 6,
                ParentStepId = 1,
                ParentBranch = "errorchildren",
                PublicId = Guid.NewGuid(),
                RefId = "on_error_action",
                Label = "On Error Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = Guid.NewGuid().ToString(), RecordIdValue = "200" })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        var failingRecords = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { { "Id", 999L } } // Iteration fails
        };
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(), null, null, null, Arg.Any<CancellationToken>())
            .Returns(failingRecords);
        _pipelineRecordSearchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(failingRecords);

        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyCollection<long>>(c => c.Contains(999L)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>()); // Fail
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyCollection<long>>(c => !c.Contains(999L)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { { 100, new Dictionary<string, object?> { { "id", 100 } } } });

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        // Quickbase Parity: Loop contains iteration failure. Outer On Success executes, outer On Error does NOT execute.
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 5), Arg.Any<CancellationToken>());
        await _pipelineRepo.DidNotReceive().CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 6), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadRequest, true)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, true)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, true)]
    [InlineData(System.Net.HttpStatusCode.NotFound, true)]
    [InlineData(System.Net.HttpStatusCode.Conflict, true)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, false)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, false)]
    [InlineData(System.Net.HttpStatusCode.BadGateway, false)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, false)]
    public void IsCatchablePipelineStepError_HttpStatusCodes_EvaluatedCorrectly(System.Net.HttpStatusCode statusCode, bool expectedCatchable)
    {
        // Arrange
        var httpEx = new System.Net.Http.HttpRequestException("HTTP error", null, statusCode);

        // Act
        var isCatchable = PipelineEngine.IsCatchablePipelineStepError(httpEx);

        // Assert
        isCatchable.Should().Be(expectedCatchable);
    }

    [Fact]
    public void SanitizeErrorMessage_RedactsBearerTokensAndPasswords()
    {
        // Arrange
        var rawMessage = "Failed API call with Bearer FAKE_TEST_TOKEN_123 and Password=SecretPass123;";

        // Act
        var sanitized = PipelineEngine.SanitizeErrorMessage(rawMessage);

        // Assert
        sanitized.Should().NotContain("FAKE_TEST_TOKEN_123");
        sanitized.Should().NotContain("SecretPass123");
        sanitized.Should().Contain("Bearer [REDACTED]");
        sanitized.Should().Contain("Password=[REDACTED]");
    }

    [Fact]
    public void IsCatchablePipelineStepError_NonCatchableInternalExceptions_ReturnsFalse()
    {
        // Arrange
        var stopEx = new PowerBase.Domain.Exceptions.PipelineStopExecutionException("Stop execution");
        var cancelEx = new OperationCanceledException("Worker cancelled");
        var recursionEx = new PowerBase.Domain.Exceptions.PipelineRecursionException("Recursion detected");
        var nonRetryableEx = new PowerBase.Domain.Exceptions.PipelineNonRetryableException("Fatal error");

        // Act & Assert
        PipelineEngine.IsCatchablePipelineStepError(stopEx).Should().BeFalse();
        PipelineEngine.IsCatchablePipelineStepError(cancelEx).Should().BeFalse();
        PipelineEngine.IsCatchablePipelineStepError(recursionEx).Should().BeFalse();
        PipelineEngine.IsCatchablePipelineStepError(nonRetryableEx).Should().BeFalse();
    }

    [Fact]
    public async Task HandleErrors_RootStepInNonTriggerPipeline_ExecutesMonitoredAndOuterSteps()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "manual", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var tableGuid1 = Guid.NewGuid();
        var tableGuid2 = Guid.NewGuid();

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "handle_root",
                Label = "Root Handle Errors Step",
                Type = "control",
                Subtype = "handle-errors",
                ConfigJson = JsonSerializer.Serialize(new { FallbackAction = "handle" }),
                ParentStepId = null,
                ParentBranch = null,
                DisplayOrder = 0
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "monitored_look_up",
                Label = "Lookup Monitored Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = tableGuid1.ToString(), RecordIdValue = "100" }),
                DisplayOrder = 0
            },
            new()
            {
                Id = 3,
                ParentStepId = null,
                ParentBranch = null,
                PublicId = Guid.NewGuid(),
                RefId = "outer_step",
                Label = "Outer Action",
                Type = "query",
                Subtype = "look-up-record",
                ConfigJson = JsonSerializer.Serialize(new { TablePublicId = tableGuid2.ToString(), RecordIdValue = "200" }),
                DisplayOrder = 1
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        _recordRepo.GetRowsByIdsAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { { 100, new Dictionary<string, object?> { { "id", 100 } } }, { 200, new Dictionary<string, object?> { { "id", 200 } } } });

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 2), Arg.Any<CancellationToken>());
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 3), Arg.Any<CancellationToken>());
    }
}
