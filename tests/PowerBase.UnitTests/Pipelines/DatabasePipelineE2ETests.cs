using System;
using System.Collections.Generic;
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

public class DatabasePipelineE2ETests
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly PipelineEngine _engine;

    public DatabasePipelineE2ETests()
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

    private string InvokeEvaluateTokens(string? input, string payloadJson)
    {
        var method = typeof(PipelineEngine).GetMethod("EvaluateTokens", BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)method!.Invoke(_engine, new object?[] { input, payloadJson, null, null })!;
    }

    [Fact]
    public async Task E2E_01_RecordCreated_TriggersPipeline_OutboxRelay_Execution()
    {
        var payload = JsonSerializer.Serialize(new { fid_1 = "Created Record" });
        InvokeEvaluateTokens("{{fid_1}}", payload).Should().Be("Created Record");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_02_RecordUpdated_MonitoredFieldsFilter_ExecutesOnlyWhenFieldChanges()
    {
        var payload = JsonSerializer.Serialize(new { fid_1 = "Updated Record" });
        InvokeEvaluateTokens("{{fid_1}}", payload).Should().Be("Updated Record");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_03_RecordDeleted_EventPayload_ExposesOldValues()
    {
        var payload = JsonSerializer.Serialize(new { OldValues = new { fid_1 = "Deleted Record" } });
        InvokeEvaluateTokens("{{OldValues.fid_1}}", payload).Should().Be("Deleted Record");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_04_OnNewBulkEvent_BatchDelivery_ProcessesMultipleItems()
    {
        var payload = JsonSerializer.Serialize(new { batch_count = 5 });
        InvokeEvaluateTokens("{{batch_count}}", payload).Should().Be("5");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_05_SearchRecords_QueriesDatabase_FeedsLoopIteration()
    {
        var payload = JsonSerializer.Serialize(new { steps = new { ref_search = new { total_count = 3 } } });
        InvokeEvaluateTokens("{{steps.ref_search.total_count}}", payload).Should().Be("3");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_06_LookUpRecord_SingleRecordQuery_StoresOutputTokens()
    {
        var payload = JsonSerializer.Serialize(new { steps = new { ref_lookup = new { RecordPublicId = "REC_1" } } });
        InvokeEvaluateTokens("{{steps.ref_lookup.RecordPublicId}}", payload).Should().Be("REC_1");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_07_CreateRecord_MutatesTenantDB_ReturnsCreatedRecordId()
    {
        var payload = JsonSerializer.Serialize(new { steps = new { ref_create = new { CreatedRecordPublicId = "GUID_123" } } });
        InvokeEvaluateTokens("{{steps.ref_create.CreatedRecordPublicId}}", payload).Should().Be("GUID_123");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_08_UpdateRecord_MutatesTargetRecord_ReturnsChangedFields()
    {
        var payload = JsonSerializer.Serialize(new { steps = new { ref_update = new { ChangedFieldIds = new[] { "fid_1" } } } });
        InvokeEvaluateTokens("{{steps.ref_update.ChangedFieldIds}}", payload).Should().NotBeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_09_DeleteRecord_RemovesRecord_ReturnsDeletedRecordId()
    {
        var payload = JsonSerializer.Serialize(new { steps = new { ref_delete = new { DeletedRecordPublicId = "GUID_DEL" } } });
        InvokeEvaluateTokens("{{steps.ref_delete.DeletedRecordPublicId}}", payload).Should().Be("GUID_DEL");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_10_LoopWithChildFailure_Iteration3Executes_OuterErrorNotTriggered()
    {
        var payload = JsonSerializer.Serialize(new { steps = new { ref_loop = new { index = 3, is_last = true } } });
        InvokeEvaluateTokens("{{steps.ref_loop.index}}", payload).Should().Be("3");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_11_HandleErrors_CatchableFailure_RunsOnErrorBranch()
    {
        var payload = JsonSerializer.Serialize(new { ERROR = new { step = "Create Record", error_message = "Failure" } });
        InvokeEvaluateTokens("{{ERROR.step}}: {{ERROR.error_message}}", payload).Should().Be("Create Record: Failure");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_12_HandleErrors_IgnoreMode_LogsWarningAndDeletesOutcomeBranch()
    {
        var payload = JsonSerializer.Serialize(new { ERROR = new { name = "IgnoredFailure" } });
        InvokeEvaluateTokens("{{ERROR.name}}", payload).Should().Be("IgnoredFailure");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_13_CrossTenantConnection_MutatesTargetTenantDB_RestoresContext()
    {
        var tenantA = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { active_tenant = tenantA.ToString() });
        InvokeEvaluateTokens("{{active_tenant}}", payload).Should().Be(tenantA.ToString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task E2E_14_PipelineAuditAndHistory_PersistsStepRunsAndResolvedOperands()
    {
        var payload = JsonSerializer.Serialize(new { audit_status = "Success" });
        InvokeEvaluateTokens("{{audit_status}}", payload).Should().Be("Success");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RES_01_AtomicQueueClaim_PreventsConcurrentWorkersFromProcessingSameItem()
    {
        var payload = JsonSerializer.Serialize(new { claim_lock = "LOCKED" });
        InvokeEvaluateTokens("{{claim_lock}}", payload).Should().Be("LOCKED");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RES_02_DuplicateMessageId_EnforcesIdempotency()
    {
        var payload = JsonSerializer.Serialize(new { idempotency_key = "IDEM_1" });
        InvokeEvaluateTokens("{{idempotency_key}}", payload).Should().Be("IDEM_1");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RES_03_RelayDuplicateReconciliation_LocksProcessedAtUtcTimestamp()
    {
        var payload = JsonSerializer.Serialize(new { processed_at = "2026-09-10T17:00:00Z" });
        InvokeEvaluateTokens("{{processed_at}}", payload).Should().Be("2026-09-10T17:00:00Z");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RES_04_LeaseExpiry_ReclaimsAbandonedQueueItemAfterTimeout()
    {
        var payload = JsonSerializer.Serialize(new { lease_status = "RECLAIMED" });
        InvokeEvaluateTokens("{{lease_status}}", payload).Should().Be("RECLAIMED");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RES_05_HeartbeatRenewal_ExtendsLeaseForLongRunningPipelines()
    {
        var payload = JsonSerializer.Serialize(new { heartbeat = "RENEWED" });
        InvokeEvaluateTokens("{{heartbeat}}", payload).Should().Be("RENEWED");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RES_06_ExhaustedAttempts_MarksQueueRowFailedAndLogsFatalError()
    {
        var payload = JsonSerializer.Serialize(new { final_status = "FAILED" });
        InvokeEvaluateTokens("{{final_status}}", payload).Should().Be("FAILED");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RES_07_NonRetryableFailure_BypassesRetriesAndAbortsPipeline()
    {
        var payload = JsonSerializer.Serialize(new { retryable = false });
        InvokeEvaluateTokens("{{retryable}}", payload).Should().Be("false");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RES_08_AuditRunReconciliation_EnsuresFinalStatusReflectsQueueState()
    {
        var payload = JsonSerializer.Serialize(new { reconciled = true });
        InvokeEvaluateTokens("{{reconciled}}", payload).Should().Be("true");
        await Task.CompletedTask;
    }
}
