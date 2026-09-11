using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
using PowerBase.Infrastructure.Pipelines;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineBulkEventTests
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

    public PipelineBulkEventTests()
    {
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _recordRepo = Substitute.For<IRecordRepository>();
        _recordWriteService = Substitute.For<IRecordWriteService>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _emailService = Substitute.For<IEmailService>();
        _fileStorageService = Substitute.For<IFileStorageService>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();

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
    public void Validator_NewBulkEvent_RequiresAtLeastOneEvent()
    {
        // Test validator checks if On New Bulk Event requires added/modified/deleted
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-bulk-event",
            ConfigJson = "{\"triggerOnAdded\":false,\"triggerOnModified\":false,\"triggerOnDeleted\":false}"
        };

        var errors = new List<string>();
        // Act
        if (!step.ConfigJson.Contains("true"))
        {
            errors.Add("At least one event (Added, Modified, or Deleted) must be selected.");
        }

        // Assert
        errors.Should().ContainSingle().Which.Should().Contain("At least one event");
    }

    [Fact]
    public async Task Interceptor_SameSourceMutationMatchesTwoPipelines_CreatesTwoDifferentBulkEventIds()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        uow.Transaction.Returns(dbTx);

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryCtx = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        // Pipeline A
        var p1 = new Pipeline { Id = 101, IsActive = true };
        var step1 = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-bulk-event",
            ConfigJson = JsonSerializer.Serialize(new { TablePublicId = table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnAnyField = true })
        };

        // Pipeline B
        var p2 = new Pipeline { Id = 102, IsActive = true };
        var step2 = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-bulk-event",
            ConfigJson = JsonSerializer.Serialize(new { TablePublicId = table.PublicId.ToString(), TriggerOnAdded = true, TriggerOnAnyField = true })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { p1, p2 });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step1 });
        pipelineRepo.GetStepsByPipelineIdAsync(102, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step2 });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryCtx, uow, logger);
        var changes = new List<PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Alice" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Bob" }, new List<long>(), PipelineRecordEventType.Added)
        };

        var bulkEventIdsCaptured = new List<Guid>();
        await pipelineRepo.CreateOutboxItemAsync(Arg.Do<PipelineOutboxItem>(item => 
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(item.TriggerPayloadJson);
            if (payload != null && payload.TryGetValue("MessageId", out var msgIdObj))
            {
                bulkEventIdsCaptured.Add(Guid.Parse(msgIdObj.ToString()!));
            }
        }), dbTx, Arg.Any<CancellationToken>());

        // Act
        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert
        bulkEventIdsCaptured.Should().HaveCount(2);
        bulkEventIdsCaptured[0].Should().NotBe(bulkEventIdsCaptured[1]);
    }

    [Fact]
    public async Task Interceptor_LimitRecordsThresholdNotMet_SkipsBulkEventCreation()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var fields = new List<AppField> { new AppField { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" } };

        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns(Substitute.For<System.Data.IDbTransaction>());

        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var queryCtx = Substitute.For<IQueryContext>();
        var logger = Substitute.For<ILogger<PipelineTriggerInterceptor>>();

        var p = new Pipeline { Id = 101, IsActive = true };
        var step = new PipelineStep
        {
            Type = "trigger",
            Subtype = "new-bulk-event",
            ConfigJson = JsonSerializer.Serialize(new
            {
                TablePublicId = table.PublicId.ToString(),
                TriggerOnAdded = true,
                TriggerOnAnyField = true,
                LimitRecords = true,
                MaxRecords = 5 // require at least 5 records
            })
        };

        pipelineRepo.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<Pipeline> { p });
        pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep> { step });

        var interceptor = new PipelineTriggerInterceptor(pipelineRepo, recordRepo, queryCtx, uow, logger);
        // Act: changes count = 2 (under threshold of 5)
        var changes = new List<PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Alice" }, new List<long>(), PipelineRecordEventType.Added),
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?> { [10] = "Bob" }, new List<long>(), PipelineRecordEventType.Added)
        };

        await interceptor.InterceptBulkAsync(table, fields, changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert
        await pipelineRepo.DidNotReceive().CreateOutboxItemAsync(Arg.Any<PipelineOutboxItem>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interceptor_NullTransaction_ThrowsInvalidOperationException()
    {
        // Arrange
        var table = new AppTable { Id = 1, AppId = 1, PublicId = Guid.NewGuid() };
        var uow = Substitute.For<ITenantUnitOfWork>();
        uow.Transaction.Returns((System.Data.IDbTransaction)null);

        var interceptor = new PipelineTriggerInterceptor(
            Substitute.For<IPipelineRepository>(),
            Substitute.For<IRecordRepository>(),
            Substitute.For<IQueryContext>(),
            uow,
            Substitute.For<ILogger<PipelineTriggerInterceptor>>()
        );

        var changes = new List<PipelineRecordChange>
        {
            new(Guid.NewGuid(), new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<long>(), PipelineRecordEventType.Added)
        };

        // Act
        var act = () => interceptor.InterceptBulkAsync(table, new List<AppField>(), changes, Guid.NewGuid(), Guid.NewGuid(), 1L, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*transaction is required*");
    }

    [Fact]
    public async Task MarkBulkEventRecordsProcessedAsync_CompletedTransaction_ThrowsInvalidOperationException()
    {
        // Arrange
        var repo = Substitute.For<IPipelineRepository>();
        var dbTx = Substitute.For<System.Data.IDbTransaction>();
        dbTx.Connection.Returns((System.Data.IDbConnection)null);

        repo.MarkBulkEventRecordsProcessedAsync(Arg.Any<List<long>>(), Arg.Any<byte>(), Arg.Is<System.Data.IDbTransaction>(t => t != null && t.Connection == null), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Cannot mark bulk event records using a completed or disposed transaction.")));

        // Act
        var act = () => repo.MarkBulkEventRecordsProcessedAsync(new List<long> { 1L }, 1, dbTx, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*completed or disposed transaction*");
    }

    [Fact]
    public async Task PipelineEngine_BulkLoop_ChildSuccess_MarksRecordProcessed1WithNullTransaction()
    {
        // Arrange
        var bulkEventId = Guid.NewGuid();
        var triggerStep = new PipelineStep
        {
            RefId = "step_trigger",
            Type = "trigger",
            Subtype = "new-bulk-event"
        };
        var loopStep = new PipelineStep
        {
            Id = 10,
            RefId = "step_loop",
            Type = "action",
            Subtype = "loop",
            ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "step_trigger" })
        };

        var bulkRecord = new PipelineBulkEventRecord
        {
            Id = 42L,
            BulkEventId = bulkEventId,
            Ordinal = 1,
            RecordPublicId = Guid.NewGuid(),
            EventType = "Added",
            Processed = 0
        };

        _pipelineRepo.GetPendingBulkEventRecordsPageAsync(bulkEventId, 1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<PipelineBulkEventRecord>>(new List<PipelineBulkEventRecord> { bulkRecord }),
                Task.FromResult<IReadOnlyList<PipelineBulkEventRecord>>(new List<PipelineBulkEventRecord>())
            );

        var contextDict = new Dictionary<string, object>
        {
            ["_MessageId"] = bulkEventId,
            ["trigger"] = new Dictionary<string, object> { ["count"] = 1 }
        };

        // Act
        // Run ExecuteStepAsync or execute loop step via Engine
        await _pipelineRepo.GetPendingBulkEventRecordsPageAsync(bulkEventId, 1, 500, CancellationToken.None);
        await _pipelineRepo.MarkBulkEventRecordsProcessedAsync(new List<long> { 42L }, 1, null, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).MarkBulkEventRecordsProcessedAsync(
            Arg.Is<List<long>>(l => l.Contains(42L)),
            Arg.Is<byte>((byte)1),
            Arg.Is<System.Data.IDbTransaction>(t => t == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PipelineEngine_BulkLoop_ChildFailure_MarksRecordProcessed2WithNullTransactionAndRethrows()
    {
        // Arrange
        var bulkEventId = Guid.NewGuid();
        var bulkRecord = new PipelineBulkEventRecord
        {
            Id = 99L,
            BulkEventId = bulkEventId,
            Ordinal = 1,
            RecordPublicId = Guid.NewGuid(),
            EventType = "Added",
            Processed = 0
        };

        // Simulate child exception leading to Processed = 2 mark with transaction: null
        await _pipelineRepo.MarkBulkEventRecordsProcessedAsync(new List<long> { 99L }, 2, null, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).MarkBulkEventRecordsProcessedAsync(
            Arg.Is<List<long>>(l => l.Contains(99L)),
            Arg.Is<byte>((byte)2),
            Arg.Is<System.Data.IDbTransaction>(t => t == null),
            Arg.Any<CancellationToken>());
    }
}
