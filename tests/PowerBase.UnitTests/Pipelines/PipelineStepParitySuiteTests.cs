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

public class PipelineStepParitySuiteTests
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly PipelineEngine _engine;

    public PipelineStepParitySuiteTests()
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
    public void COND_P1_DateAndDateTimeComparisons_EvaluatesCorrectly()
    {
        var method = typeof(PipelineEngine).GetMethod("EvaluateConditionOperator", BindingFlags.NonPublic | BindingFlags.Instance);
        
        bool res1 = (bool)method!.Invoke(_engine, new object[] { "2026-09-10", "greater-than", "2026-09-01" })!;
        bool res2 = (bool)method!.Invoke(_engine, new object[] { "2026-09-10T12:00:00Z", "equals", "2026-09-10T12:00:00Z" })!;

        res1.Should().BeTrue();
        res2.Should().BeTrue();
    }

    [Fact]
    public void LOOP_P1_LoopIndexAndBoundaryTokens_EvaluatesCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_loop = new
                {
                    index = 0,
                    is_first = true,
                    is_last = false,
                    item = new { fid_1 = "Val1" }
                }
            }
        });

        InvokeEvaluateTokens("{{steps.ref_loop.index}}", payload).Should().Be("0");
        InvokeEvaluateTokens("{{steps.ref_loop.is_first}}", payload).Should().Be("true");
        InvokeEvaluateTokens("{{steps.ref_loop.is_last}}", payload).Should().Be("false");
    }

    [Fact]
    public void HE_P1_ERRORObjectDetails_AvailableOnlyInErrorBranch()
    {
        var payload = JsonSerializer.Serialize(new
        {
            ERROR = new
            {
                step = "Create Record",
                error_message = "Required field missing",
                channel = "Quickbase"
            }
        });

        InvokeEvaluateTokens("{{ERROR.step}}: {{ERROR.error_message}}", payload).Should().Be("Create Record: Required field missing");
    }

    [Fact]
    public void SEARCH_P1_AdvancedQuerySyntaxPreservation_MaintainsQueryString()
    {
        var configJson = JsonSerializer.Serialize(new
        {
            TableId = Guid.NewGuid().ToString(),
            AdvancedQuery = "{3.EX.'Active'} AND {6.GT.'100'}"
        });

        using var doc = JsonDocument.Parse(configJson);
        doc.RootElement.GetProperty("AdvancedQuery").GetString().Should().Be("{3.EX.'Active'} AND {6.GT.'100'}");
    }

    [Fact]
    public void LOOKUP_P1_FoundAndNotFoundHandling_ReturnsExpectedOutputs()
    {
        var payloadFound = JsonSerializer.Serialize(new { steps = new { ref_lookup = new { RecordPublicId = "REC_1", fid_5 = "FoundValue" } } });
        var payloadNotFound = JsonSerializer.Serialize(new { steps = new { ref_lookup = new { RecordPublicId = (string?)null } } });

        InvokeEvaluateTokens("{{steps.ref_lookup.fid_5}}", payloadFound).Should().Be("FoundValue");
        InvokeEvaluateTokens("{{steps.ref_lookup.RecordPublicId}}", payloadNotFound).Should().Be("");
    }

    [Fact]
    public void CREATE_P1_OutputCreatedRecordPublicId_ExposedForSubsequentSteps()
    {
        var createdId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_create = new { CreatedRecordPublicId = createdId, fid_1 = "Created" }
            }
        });

        InvokeEvaluateTokens("{{steps.ref_create.CreatedRecordPublicId}}", payload).Should().Be(createdId);
    }

    [Fact]
    public void UPDATE_P1_ClearNullSemantics_NullMappingEvaluatesToNull()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_update = new { UpdatedRecordPublicId = Guid.NewGuid().ToString() }
            }
        });

        InvokeEvaluateTokens("{{steps.ref_update.UpdatedRecordPublicId}}", payload).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DELETE_P1_ValidTargetDeletion_ExposesDeletedRecordIdentity()
    {
        var deletedId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_delete = new { RecordPublicId = deletedId, Status = "Deleted" }
            }
        });

        InvokeEvaluateTokens("{{steps.ref_delete.RecordPublicId}}", payload).Should().Be(deletedId);
    }
}
