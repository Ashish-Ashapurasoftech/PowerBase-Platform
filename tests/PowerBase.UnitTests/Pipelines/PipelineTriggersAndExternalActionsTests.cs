using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.API.Controllers;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Infrastructure.Pipelines;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using System.Net.Http;
using PowerBase.Domain.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineTriggersAndExternalActionsTests
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IEmailService _emailService;
    private readonly IFileStorageService _fileStorageService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PipelineEngine _engine;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PipelineTriggersAndExternalActionsTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _recordRepo = Substitute.For<IRecordRepository>();
        _recordWriteService = Substitute.For<IRecordWriteService>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _emailService = Substitute.For<IEmailService>();
        _fileStorageService = Substitute.For<IFileStorageService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _recordWriteService.ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<bool>())
            .Returns(new Dictionary<long, object?>());

        _engine = new PipelineEngine(
            _pipelineRepo,
            _recordRepo,
            _recordWriteService,
            _tableRepo,
            _fieldRepo,
            _emailService,
            _httpClientFactory,
            _fileStorageService,
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

    [Fact]
    public async Task RunPipelineAttemptAsync_DepthExceedsLimit_DoesNotAbortExecution()
    {
        // Arrange
        var task = new PipelineExecutionTask
        {
            PipelineId = 1,
            TenantId = 1,
            TriggerEvent = "RecordAdded",
            TriggerPayloadJson = "{}",
            Depth = 11,
            CorrelationId = Guid.NewGuid().ToString()
        };

        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 1L));

        _pipelineRepo.GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep>());

        // Act
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        
        await _pipelineRepo.Received(1).UpdateRunAsync(
            Arg.Is<PipelineRun>(r => r.Status == "Skipped"),
            Arg.Any<CancellationToken>());

        await _emailService.DidNotReceive().SendRecursionAlertEmailAsync(
            Arg.Any<long>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPipelineAttemptAsync_DepthAllowed_DoesNotThrow()
    {
        // Arrange
        var task = new PipelineExecutionTask
        {
            PipelineId = 1,
            TenantId = 1,
            TriggerEvent = "RecordAdded",
            TriggerPayloadJson = "{}",
            Depth = 10,
            CorrelationId = Guid.NewGuid().ToString()
        };

        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 1L));

        _pipelineRepo.GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep>());

        // Act
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WebhookController_MissingToken_Returns401()
    {
        // Arrange
        var adminRepo = Substitute.For<IAdminRepository>();
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        var queryCtx = Substitute.For<IQueryContext>();

        var controller = new WebhookController(adminRepo, pipelineRepo, queue, queryCtx, Substitute.For<ILogger<WebhookController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var tenantPublicId = Guid.NewGuid();
        var stepPublicId = Guid.NewGuid();

        adminRepo.GetTenantIdByPublicIdAsync(tenantPublicId, Arg.Any<CancellationToken>()).Returns(1L);
        pipelineRepo.GetStepByPublicIdAsync(stepPublicId, Arg.Any<CancellationToken>())
            .Returns(new PipelineStep { Subtype = "webhook", ConfigJson = "{\"authType\": \"bearer\", \"authSecret\": \"secret\"}" });

        // Act
        var result = await controller.ExecuteWebhook(tenantPublicId, stepPublicId, CancellationToken.None);

        // Assert
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task WebhookController_InvalidToken_Returns403()
    {
        // Arrange
        var adminRepo = Substitute.For<IAdminRepository>();
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        var queryCtx = Substitute.For<IQueryContext>();

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Headers["Authorization"] = "Bearer wrong-secret";

        var controller = new WebhookController(adminRepo, pipelineRepo, queue, queryCtx, Substitute.For<ILogger<WebhookController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpCtx }
        };

        var tenantPublicId = Guid.NewGuid();
        var stepPublicId = Guid.NewGuid();

        adminRepo.GetTenantIdByPublicIdAsync(tenantPublicId, Arg.Any<CancellationToken>()).Returns(1L);
        pipelineRepo.GetStepByPublicIdAsync(stepPublicId, Arg.Any<CancellationToken>())
            .Returns(new PipelineStep { Subtype = "webhook", ConfigJson = "{\"authType\": \"bearer\", \"authSecret\": \"secret\"}" });

        // Act
        var result = await controller.ExecuteWebhook(tenantPublicId, stepPublicId, CancellationToken.None);

        // Assert
        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task WebhookController_InvalidSchema_Returns400()
    {
        // Arrange
        var adminRepo = Substitute.For<IAdminRepository>();
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        var queryCtx = Substitute.For<IQueryContext>();

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Headers["Authorization"] = "Bearer secret";
        var bodyBytes = Encoding.UTF8.GetBytes("{\"age\": \"not-an-integer\"}");
        httpCtx.Request.Body = new MemoryStream(bodyBytes);

        var controller = new WebhookController(adminRepo, pipelineRepo, queue, queryCtx, Substitute.For<ILogger<WebhookController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpCtx }
        };

        var tenantPublicId = Guid.NewGuid();
        var stepPublicId = Guid.NewGuid();

        var schemaJson = "{\"type\": \"object\", \"properties\": {\"age\": {\"type\": \"integer\"}}, \"required\": [\"age\"]}";

        adminRepo.GetTenantIdByPublicIdAsync(tenantPublicId, Arg.Any<CancellationToken>()).Returns(1L);
        pipelineRepo.GetStepByPublicIdAsync(stepPublicId, Arg.Any<CancellationToken>())
            .Returns(new PipelineStep { Subtype = "webhook", ConfigJson = $"{{\"authType\": \"bearer\", \"authSecret\": \"secret\", \"jsonSchema\": {JsonSerializer.Serialize(schemaJson)}}}" });

        // Act
        var result = await controller.ExecuteWebhook(tenantPublicId, stepPublicId, CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task WebhookController_ValidPayload_EnqueuesTaskAndReturnsAccepted()
    {
        // Arrange
        var adminRepo = Substitute.For<IAdminRepository>();
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var queue = Substitute.For<IPipelineExecutionQueue>();
        var queryCtx = Substitute.For<IQueryContext>();

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Headers["Authorization"] = "Bearer secret";
        var bodyBytes = Encoding.UTF8.GetBytes("{\"age\": 30}");
        httpCtx.Request.Body = new MemoryStream(bodyBytes);

        var controller = new WebhookController(adminRepo, pipelineRepo, queue, queryCtx, Substitute.For<ILogger<WebhookController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpCtx }
        };

        var tenantPublicId = Guid.NewGuid();
        var stepPublicId = Guid.NewGuid();

        var schemaJson = "{\"type\": \"object\", \"properties\": {\"age\": {\"type\": \"integer\"}}, \"required\": [\"age\"]}";

        adminRepo.GetTenantIdByPublicIdAsync(tenantPublicId, Arg.Any<CancellationToken>()).Returns(1L);
        pipelineRepo.GetStepByPublicIdAsync(stepPublicId, Arg.Any<CancellationToken>())
            .Returns(new PipelineStep { Subtype = "webhook", ConfigJson = $"{{\"authType\": \"bearer\", \"authSecret\": \"secret\", \"jsonSchema\": {JsonSerializer.Serialize(schemaJson)}}}" });

        // Act
        var result = await controller.ExecuteWebhook(tenantPublicId, stepPublicId, CancellationToken.None);

        // Assert
        result.Should().BeOfType<AcceptedResult>();
        queue.Received(1).QueueTask(Arg.Is<PipelineExecutionTask>(t => t.TenantId == 1 && t.TriggerEvent == "webhook"));
    }

    [Fact]
    public async Task ExecuteStepAsync_SearchRecords_ExecutesQueryAndReturnsRecordsInOutput()
    {
        // Arrange
        var step = new PipelineStep
        {
            Id = 100,
            RefId = "search_ref",
            Type = "query",
            Subtype = "search-records",
            ConfigJson = "{\"tableId\":\"372e0f07-5d92-f111-bbf5-002324be71d7\",\"filterField\":\"Name\",\"filterValue\":\"John\",\"maxResults\":5}"
        };

        var table = new AppTable { Id = 1, PublicId = Guid.Parse("372e0f07-5d92-f111-bbf5-002324be71d7") };
        var fields = new List<AppField>
        {
            new AppField { Id = 10, Fid = 1, Name = "Name" }
        };

        _tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        var queryResults = new List<Dictionary<string, object?>>
        {
            new() { { "Name", "John" } }
        };
        _recordRepo.ListAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(queryResults);

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(_engine, new object[] { step, "{}", new Dictionary<string, object>(), new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "trigger_1", CancellationToken.None })!;
        var output = await task;

        // Assert
        output.Should().Contain("records");
        output.Should().Contain("John");
    }



    [Fact]
    public async Task Engine_FailedRunRetry_DoesNotAckBeforeExecutionFinishes()
    {
        var task = new PipelineExecutionTask
        {
            PipelineId = 1,
            TenantId = 1,
            TriggerEvent = "new-event",
            MessageId = Guid.NewGuid().ToString()
        };

        var run = new PipelineRun { Id = 1, AttemptCount = 1, Status = "Failed" };
        _pipelineRepo.GetRunByMessageIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(run);
        _pipelineRepo.ClaimFailedRunRetryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _pipelineRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new Pipeline { IsActive = true });
        _pipelineRepo.GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { new PipelineStep { Type = "trigger", Subtype = "new-event", ConfigJson = "{}" } });

        // Verify that ExecuteAsync executes the engine and doesn't return early
        await _engine.ExecuteAsync(task, CancellationToken.None);
        
        await _pipelineRepo.Received(1).ClaimFailedRunRetryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Engine_FailedRunRetryExhausted_AcksWithoutExecution()
    {
        var task = new PipelineExecutionTask
        {
            PipelineId = 1,
            TenantId = 1,
            TriggerEvent = "new-event",
            MessageId = Guid.NewGuid().ToString()
        };

        var run = new PipelineRun { Id = 1, AttemptCount = 5, Status = "Failed" };
        _pipelineRepo.GetRunByMessageIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(run);

        await _engine.ExecuteAsync(task, CancellationToken.None);

        await _pipelineRepo.DidNotReceive().ClaimFailedRunRetryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Engine_StaleRunningRun_ReclaimIncrementsAttemptCountAtomically()
    {
        var task = new PipelineExecutionTask
        {
            PipelineId = 1,
            TenantId = 1,
            TriggerEvent = "new-event",
            MessageId = Guid.NewGuid().ToString()
        };

        var run = new PipelineRun { Id = 1, AttemptCount = 1, Status = "Running", LockedUntil = DateTime.UtcNow.AddMinutes(-5) };
        _pipelineRepo.GetRunByMessageIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(run);
        _pipelineRepo.ReclaimStaleRunAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _pipelineRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new Pipeline { IsActive = true });
        _pipelineRepo.GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { new PipelineStep { Type = "trigger", Subtype = "new-event", ConfigJson = "{}" } });

        await _engine.ExecuteAsync(task, CancellationToken.None);

        await _pipelineRepo.Received(1).ReclaimStaleRunAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Engine_StaleReclaimAffectedRowsZero_DoesNotExecute()
    {
        var task = new PipelineExecutionTask
        {
            PipelineId = 1,
            TenantId = 1,
            TriggerEvent = "new-event",
            MessageId = Guid.NewGuid().ToString()
        };

        var run = new PipelineRun { Id = 1, AttemptCount = 1, Status = "Running", LockedUntil = DateTime.UtcNow.AddMinutes(-5) };
        _pipelineRepo.GetRunByMessageIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(run);
        _pipelineRepo.ReclaimStaleRunAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        await _engine.ExecuteAsync(task, CancellationToken.None);

        await _pipelineRepo.DidNotReceive().GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_NBelowMax_InsertsOutboxRows()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        // Set up active pipeline trigger with maxRecords = 5
        var pipeline = new Pipeline { Id = 101, IsActive = true, IsDeleted = false };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                LimitRecords = true,
                MaxRecords = 5,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Pipeline> { pipeline });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);

        // N=3 records (below max = 5)
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Alice" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Bob" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Charlie" }, new List<long>(), PipelineRecordEventType.Added)
        };

        // Act
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert: All 3 records should be inserted as outbox rows
        await pipelineRepo.Received(3).CreateOutboxItemAsync(Arg.Any<PowerBase.Domain.Entities.PipelineOutboxItem>(), dbTx, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_NAboveMax_SkipsAll()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        // Set up active pipeline trigger with maxRecords = 5
        var pipeline = new Pipeline { Id = 101, IsActive = true, IsDeleted = false };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                LimitRecords = true,
                MaxRecords = 5,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Pipeline> { pipeline });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);

        // N=6 records (above max = 5)
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "A" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "B" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "C" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "D" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "E" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "F" }, new List<long>(), PipelineRecordEventType.Added)
        };

        // Act
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert: Zero outbox rows should be created
        await pipelineRepo.DidNotReceive().CreateOutboxItemAsync(Arg.Any<PowerBase.Domain.Entities.PipelineOutboxItem>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_N10Max5Matching2_SkipsAll()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        // Set up active pipeline trigger with maxRecords = 5, trigger on triggerFields = [10] (if Name is modified)
        var pipeline = new Pipeline { Id = 101, IsActive = true, IsDeleted = false };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnModified = true,
                LimitRecords = true,
                MaxRecords = 5,
                TriggerOnAnyField = false,
                TriggerFields = new[] { "fid_10" }
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Pipeline> { pipeline });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);

        // N=10 records logical bulk changes, but only 2 have name field modified
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>();
        for (int i = 1; i <= 10; i++)
        {
            // If i <= 2, field 10 (Name) changed from "Old" to "New"
            var before = new Dictionary<long, object?> { [10] = "Old" };
            var after = new Dictionary<long, object?> { [10] = (i <= 2) ? "New" : "Old" };
            var changed = (i <= 2) ? new List<long> { 10 } : new List<long>();

            changes.Add(new PowerBase.Application.Common.Models.PipelineRecordChange(
                Guid.NewGuid(), before, after, changed, PipelineRecordEventType.Modified
            ));
        }

        // Act
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert: N=10 exceeds max=5, so even though matching=2, total logical count N decides. Zero outbox rows created.
        await pipelineRepo.DidNotReceive().CreateOutboxItemAsync(Arg.Any<PowerBase.Domain.Entities.PipelineOutboxItem>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_TwoPipelinesDifferentLimits_OneSkipsOneExecutes()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        // Pipeline 1: maxRecords = 5 (will skip since N=10)
        var pipeline1 = new Pipeline { Id = 101, IsActive = true, IsDeleted = false };
        var step1 = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                LimitRecords = true,
                MaxRecords = 5,
                TriggerOnAnyField = true
            })
        };

        // Pipeline 2: maxRecords = 15 (will execute since N=10)
        var pipeline2 = new Pipeline { Id = 102, IsActive = true, IsDeleted = false };
        var step2 = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                LimitRecords = true,
                MaxRecords = 15,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Pipeline> { pipeline1, pipeline2 });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep> { step1 });
        pipelineRepo.GetStepsByPipelineIdAsync(102, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineStep> { step2 });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);

        // N=10 records
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>();
        for (int i = 0; i < 10; i++)
        {
            changes.Add(new PowerBase.Application.Common.Models.PipelineRecordChange(
                Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = $"User_{i}" }, new List<long>(), PipelineRecordEventType.Added
            ));
        }

        // Act
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert: Expect exactly 10 outbox inserts, all for PipelineId = 102
        await pipelineRepo.Received(10).CreateOutboxItemAsync(Arg.Is<PowerBase.Domain.Entities.PipelineOutboxItem>(item => item.PipelineId == 102), dbTx, Arg.Any<CancellationToken>());
        await pipelineRepo.DidNotReceive().CreateOutboxItemAsync(Arg.Is<PowerBase.Domain.Entities.PipelineOutboxItem>(item => item.PipelineId == 101), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_TransactionNull_ThrowsInvalidOperationException()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField>();

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns((System.Data.IDbTransaction)null); // Null transaction

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<long>(), PipelineRecordEventType.Added)
        };

        // Act
        var act = () => interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("An active transaction is required to write to the pipeline outbox. Ensure the mutation is wrapped inside an active unit of work transaction.");
    }

    [Fact]
    public async Task Interceptor_NEqualsMax_InsertsMatchingRows()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var pipeline = new Pipeline { Id = 101, IsActive = true, IsDeleted = false };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                LimitRecords = true,
                MaxRecords = 3,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { pipeline });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "A" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "B" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "C" }, new List<long>(), PipelineRecordEventType.Added)
        };

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        await pipelineRepo.Received(3).CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), dbTx, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_NRecordsMPipelines_InsertsExactRows()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var p1 = new Pipeline { Id = 101, IsActive = true };
        var p2 = new Pipeline { Id = 102, IsActive = true };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { p1, p2 });
        pipelineRepo.GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "A" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "B" }, new List<long>(), PipelineRecordEventType.Added)
        };

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // 2 records * 2 pipelines = 4 outbox rows
        await pipelineRepo.Received(4).CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), dbTx, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_OutboxRows_HaveUniqueMessageIds()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var p = new Pipeline { Id = 101, IsActive = true };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new { TablePublicId = table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnAnyField = true })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { p });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "A" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "B" }, new List<long>(), PipelineRecordEventType.Added)
        };

        var messageIds = new HashSet<Guid>();
        await pipelineRepo.CreateOutboxItemAsync(Arg.Do<PipelineOutboxItem>(item => messageIds.Add(item.MessageId)), dbTx, Arg.Any<CancellationToken>());

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        messageIds.Count.Should().Be(2);
    }

    [Fact]
    public async Task Interceptor_PreservesBatchId()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField>();

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<System.Data.IDbTransaction>());

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var p = new Pipeline { Id = 101, IsActive = true };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new { TablePublicId = table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnAnyField = true })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { p });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<long>(), PipelineRecordEventType.Added)
        };

        var batchId = Guid.NewGuid();
        await interceptor.InterceptBulkAsync(table, fields, changes, batchId, Guid.NewGuid(), 1L, CancellationToken.None);

        await pipelineRepo.Received(1).CreateOutboxItemAsync(Arg.Is<PipelineOutboxItem>(item => item.BatchId == batchId), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_ConvertsInternalIdsToStableFids()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 456, Fid = 12, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<System.Data.IDbTransaction>());

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var p = new Pipeline { Id = 101, IsActive = true };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new { TablePublicId = table.PublicId.ToString(), TriggerOnModified = true, TriggerOnAnyField = true })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { p });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [456] = "NewVal" }, new List<long> { 456 }, PipelineRecordEventType.Modified)
        };

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        await pipelineRepo.Received(1).CreateOutboxItemAsync(
            Arg.Is<PipelineOutboxItem>(item => item.TriggerPayloadJson.Contains("fid_12")), 
            Arg.Any<System.Data.IDbTransaction>(), 
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_SelectedFieldValuesContainExpectedValues()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 101, Name = "Status", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<System.Data.IDbTransaction>());

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var p = new Pipeline { Id = 101, IsActive = true };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new { TablePublicId = table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnAnyField = true })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { p });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Pending" }, new List<long>(), PipelineRecordEventType.Added)
        };

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        await pipelineRepo.Received(1).CreateOutboxItemAsync(
            Arg.Is<PipelineOutboxItem>(item => item.TriggerPayloadJson.Contains("\"fid_101\":\"Pending\"")), 
            Arg.Any<System.Data.IDbTransaction>(), 
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_DuplicateRecordIds_UsesUniqueLogicalCount()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField>();

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<System.Data.IDbTransaction>());

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        
        var duplicateGuid = Guid.NewGuid();
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(duplicateGuid, new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<long>(), PipelineRecordEventType.Added),
            new(duplicateGuid, new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<long>(), PipelineRecordEventType.Added)
        };

        var act = () => interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);
        await act.Should().ThrowAsync<PowerBase.Domain.Exceptions.ValidationException>()
            .Where(e => e.Errors.ContainsKey("RecordPublicId"));
    }

    [Fact]
    public async Task Interceptor_MixedEventTypes_IsRejected()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField>();

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<System.Data.IDbTransaction>());

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<long>(), PipelineRecordEventType.Modified)
        };

        var act = () => interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);
        await act.Should().ThrowAsync<PowerBase.Domain.Exceptions.ValidationException>()
            .Where(e => e.Errors.ContainsKey("EventType"));
    }

    [Fact]
    public async Task CommitUpsert_ModifiedRecords_AreInterceptedOnce()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 101, Name = "Status", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var writeService = Substitute.For<IRecordWriteService>();
        writeService.ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<bool>())
            .Returns(new Dictionary<long, object?>());
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(1L);

        var engine = new PipelineEngine(
            pipelineRepo,
            recordRepo,
            writeService,
            tableRepo,
            fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            triggerInterceptor,
            uow,
            Substitute.For<IPipelineAuditFormatter>(),
            queryContext,
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        var step = new PipelineStep
        {
            Subtype = "commit-upsert",
            ConfigJson = "{\"ParentUpsertStepRefId\": \"ref_upsert\"}"
        };

        var contextDict = new Dictionary<string, object>();
        var sessions = new Dictionary<string, PipelineEngine.BulkUpsertSession>();
        contextDict["_bulkUpsertSessions"] = sessions;
        contextDict["_CreatedBy"] = 1L;

        var row = new Dictionary<long, object?> { [101] = "Active" };
        sessions["ref_upsert"] = new PipelineEngine.BulkUpsertSession
        {
            TableLabel = table.PublicId.ToString(),
            MergeKeyFid = "fid_101",
            Rows = new List<Dictionary<long, object?>> { row }
        };

        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        // Mock existing record to trigger update path
        var existingRow = new Dictionary<string, object> { { "fid_101", "Inactive" }, { "publicId", Guid.NewGuid() } };
        recordRepo.ListAsync(table, fields, 1, 1, Arg.Any<FilterGroup>(), null, null, Arg.Any<CancellationToken>()).Returns(new List<IReadOnlyDictionary<string, object>> { existingRow });

        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "trigger_1", CancellationToken.None })!;
        await task;

        // Verify writeService was called with suppressInterception = true
        await writeService.Received(1).ApplyAsync(
            table, fields, Arg.Any<Guid>(), row, AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), dbTx, suppressInterception: true);

        // Verify triggerInterceptor was called for bulk modified exactly once with UserId 1
        await triggerInterceptor.Received(1).InterceptBulkAsync(
            table, fields, Arg.Is<IReadOnlyList<PowerBase.Application.Common.Models.PipelineRecordChange>>(list => list.Count == 1), Arg.Any<Guid>(), Arg.Any<Guid>(), 1L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PipelineUpdate_UsesSameTransactionAndPreservesBeforeAfterValues()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 101, Name = "Status", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var writeService = Substitute.For<IRecordWriteService>();
        writeService.ApplyAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<bool>())
            .Returns(new Dictionary<long, object?>());
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();

        var engine = new PipelineEngine(
            pipelineRepo,
            recordRepo,
            writeService,
            tableRepo,
            fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            uow,
            Substitute.For<IPipelineAuditFormatter>(),
            Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        var step = new PipelineStep
        {
            Subtype = "update-record",
            ConfigJson = "{\"tableId\":\"" + table.PublicId + "\",\"targetRecordId\":\"{{trigger.RecordPublicId}}\",\"fieldMappings\":[{\"field\":\"fid_101\",\"value\":\"Completed\"}]}"
        };

        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        var contextDict = new Dictionary<string, object>();
        var triggerGuid = Guid.NewGuid();
        contextDict["trigger"] = new Dictionary<string, object> { { "RecordPublicId", triggerGuid.ToString() } };
        var payloadJson = JsonSerializer.Serialize(new { trigger = new { RecordPublicId = triggerGuid.ToString() } });

        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(engine, new object[] { step, payloadJson, contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "trigger_1", CancellationToken.None })!;
        await task;

        // Verify ApplyAsync was called with the active transaction
        await writeService.Received(1).ApplyAsync(
            table, fields, triggerGuid, Arg.Is<IReadOnlyDictionary<long, object?>>(dict => dict[101] as string == "Completed"), AuditActions.Updated, Arg.Any<string>(), Arg.Any<CancellationToken>(), dbTx);
    }

    [Fact]
    public async Task BulkDelete_CapturesStableFidSnapshotsBeforeDeletion()
    {
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid(), Name = "Leads" };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 101, Name = "Status", TypeCode = "Text" } };

        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var triggerInterceptor = Substitute.For<IPipelineTriggerInterceptor>();
        var enforcer = Substitute.For<IRolePermissionEnforcer>();
        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var access = new TableAccessContext { Unrestricted = true };
        enforcer.GetTableAccessAsync(table, fields, Arg.Any<CancellationToken>()).Returns(access);

        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserId.Returns(101L);

        var handler = new BulkDeleteRecordsCommandHandler(
            tableRepo, fieldRepo, recordRepo, enforcer, Substitute.For<IAuditRepository>(), Substitute.For<IRelationshipRepository>(), triggerInterceptor, uow, queryContext, Substitute.For<IMessagePublisher>()
        );

        var recordId = Guid.NewGuid();
        tableRepo.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        var recordData = new Dictionary<string, object?> { { "f_101", "New" } };
        recordRepo.GetByPublicIdAsync(table, fields, recordId, Arg.Any<CancellationToken>()).Returns(recordData);

        var command = new BulkDeleteRecordsCommand(table.PublicId, new List<Guid> { recordId });
        await handler.HandleAsync(command, CancellationToken.None);

        // Verify snapshots are captured with correct before values and stable FIDs and pass the user context (101L)
        await triggerInterceptor.Received(1).InterceptBulkAsync(
            table, fields, Arg.Is<IReadOnlyList<PowerBase.Application.Common.Models.PipelineRecordChange>>(list => list.Count == 1 && list[0].BeforeValues[10] as string == "New"), Arg.Any<Guid>(), Arg.Any<Guid>(), 101L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_CrossTenantTenantConnection_KeepsTenantPublicId()
    {
        var table = new AppTable { Id = 1, PublicId = Guid.NewGuid(), Name = "Leads" };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 101, Name = "Status", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        
        // Sequence return: 1st and 2nd for mockSubs setup (OwnerTenantId = 6, TargetTenantId = 6),
        // 3rd for isSameTenant comparison (returns 7, so OwnerTenantId (6) != CurrentTenant (7) -> cross-tenant)
        queryContext.TenantId.Returns(6L, 6L, 7L);

        var mainQueueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var connectionPublicId = Guid.NewGuid(); // represents TenantPublicId

        var pipeline = new Pipeline { Id = 101, IsActive = true, IsDeleted = false, PublicId = Guid.NewGuid() };
        var step = new PipelineStep
        {
            PublicId = Guid.NewGuid(),
            RefId = "step_1",
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = connectionPublicId.ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { pipeline });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, null!, mainQueueRepo, logger);

        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Alice" }, new List<long>(), PipelineRecordEventType.Added)
        };

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        await mainQueueRepo.Received(1).EnqueueAsync(Arg.Is<PipelineQueue>(job => 
            HasMatchingConnectionId(job.TriggerPayloadJson, connectionPublicId.ToString())
        ), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_CrossTenantSavedAccountConnection_KeepsPipelineAccountPublicId()
    {
        var table = new AppTable { Id = 1, PublicId = Guid.NewGuid(), Name = "Leads" };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 101, Name = "Status", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        
        // Sequence return for cross-tenant
        queryContext.TenantId.Returns(6L, 6L, 8L);

        var mainQueueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var pipelineAccountPublicId = Guid.NewGuid(); // represents saved PipelineAccount.PublicId

        var pipeline = new Pipeline { Id = 101, IsActive = true, IsDeleted = false, PublicId = Guid.NewGuid() };
        var step = new PipelineStep
        {
            PublicId = Guid.NewGuid(),
            RefId = "step_1",
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = pipelineAccountPublicId.ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { pipeline });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, null!, mainQueueRepo, logger);

        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Alice" }, new List<long>(), PipelineRecordEventType.Added)
        };

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        await mainQueueRepo.Received(1).EnqueueAsync(Arg.Is<PipelineQueue>(job => 
            HasMatchingConnectionId(job.TriggerPayloadJson, pipelineAccountPublicId.ToString())
        ), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    private static bool HasMatchingConnectionId(string payloadJson, string expectedGuidStr)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.TryGetProperty("ConnectionPublicId", out var prop))
        {
            return prop.GetString() == expectedGuidStr;
        }
        return false;
    }

    [Fact]
    public async Task Interceptor_SelfTriggerRecursion_DoesNotSuppressAndPropagatesChainAndDepth()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        // Setup QueryContext to mimic running Pipeline 101
        queryContext.IsPipelineExecution.Returns(true);
        queryContext.PipelineDepth.Returns(1);
        queryContext.PipelineChainJson.Returns("[101]");

        // Set up active pipeline 101 trigger
        var pipeline = new Pipeline { Id = 101, IsActive = true, IsDeleted = false };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { pipeline });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Value" }, new List<long>(), PipelineRecordEventType.Added)
        };

        // Act
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert: The pipeline is triggered again (depth=2, chain=[101, 101])
        await pipelineRepo.Received(1).CreateOutboxItemAsync(Arg.Is<PipelineOutboxItem>(item =>
            item.PipelineId == 101 &&
            item.Depth == 2 &&
            item.PipelineChain == "[101,101]"
        ), dbTx, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_CyclicRecurrence_DoesNotSuppressAndPropagatesChainAndDepth()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        // Setup QueryContext: A -> B -> A (current chain is [101, 102], executing B, writing event that will trigger A)
        queryContext.IsPipelineExecution.Returns(true);
        queryContext.PipelineDepth.Returns(2);
        queryContext.PipelineChainJson.Returns("[101,102]");

        // Set up active pipeline 101 trigger
        var pipelineA = new Pipeline { Id = 101, IsActive = true, IsDeleted = false };
        var stepA = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { pipelineA });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { stepA });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Value" }, new List<long>(), PipelineRecordEventType.Added)
        };

        // Act
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert: A triggers again (depth=3, chain=[101, 102, 101])
        await pipelineRepo.Received(1).CreateOutboxItemAsync(Arg.Is<PipelineOutboxItem>(item =>
            item.PipelineId == 101 &&
            item.Depth == 3 &&
            item.PipelineChain == "[101,102,101]"
        ), dbTx, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_DepthBoundary_Depth10Allowed_Depth11Allowed()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        // Case 1: PipelineDepth is 9 -> currentDepth will be 10 -> Allowed
        queryContext.IsPipelineExecution.Returns(true);
        queryContext.PipelineDepth.Returns(9);
        queryContext.PipelineChainJson.Returns("[101]");

        var pipeline = new Pipeline { Id = 101, IsActive = true, IsDeleted = false };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                ConnectionPublicId = Guid.NewGuid().ToString(),
                AppPublicId = Guid.NewGuid().ToString(),
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                TriggerOnAnyField = true
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { pipeline });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryContext, uow, logger);
        var changes = new List<PowerBase.Application.Common.Models.PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Val" }, new List<long>(), PipelineRecordEventType.Added)
        };

        // Act & Assert Case 1
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);
        await pipelineRepo.Received(1).CreateOutboxItemAsync(Arg.Is<PipelineOutboxItem>(item => item.Depth == 10), dbTx, Arg.Any<CancellationToken>());

        // Case 2: PipelineDepth is 10 -> currentDepth will be 11 -> Now Allowed
        queryContext.PipelineDepth.Returns(10);
        pipelineRepo.ClearReceivedCalls();

        // Act & Assert Case 2
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);
        await pipelineRepo.Received(1).CreateOutboxItemAsync(Arg.Is<PipelineOutboxItem>(item => item.Depth == 11), dbTx, Arg.Any<CancellationToken>());
    }
}

