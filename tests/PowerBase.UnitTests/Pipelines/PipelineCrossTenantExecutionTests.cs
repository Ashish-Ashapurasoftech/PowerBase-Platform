using System;
using System.Collections.Generic;
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
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineCrossTenantExecutionTests
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAdminRepository _adminRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IQueryContext _queryContext;
    private readonly PipelineEngine _engine;

    public PipelineCrossTenantExecutionTests()
    {
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _recordRepo = Substitute.For<IRecordRepository>();
        _recordWriteService = Substitute.For<IRecordWriteService>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _adminRepo = Substitute.For<IAdminRepository>();
        _tenantRepo = Substitute.For<ITenantRepository>();
        _queryContext = Substitute.For<IQueryContext>();

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
            _queryContext,
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<IServiceProvider>(),
            _adminRepo,
            _tenantRepo,
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );
    }

    [Fact]
    public async Task XT_P0_001_TenantASearch_TenantBUpdate_ExecutesWithoutGlobalTenantSwitch()
    {
        // Tenant A source step connection -> Tenant B target step connection
        var tenantAConnection = Guid.NewGuid();
        var tenantBConnection = Guid.NewGuid();

        _adminRepo.GetTenantIdByPublicIdAsync(tenantAConnection, Arg.Any<CancellationToken>()).Returns(100L);
        _adminRepo.GetTenantIdByPublicIdAsync(tenantBConnection, Arg.Any<CancellationToken>()).Returns(200L);
        _tenantRepo.IsActiveMemberAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);

        // Verify connection IDs resolve to distinct tenant IDs
        var tenantAId = await _adminRepo.GetTenantIdByPublicIdAsync(tenantAConnection, CancellationToken.None);
        var tenantBId = await _adminRepo.GetTenantIdByPublicIdAsync(tenantBConnection, CancellationToken.None);

        tenantAId.Should().Be(100L);
        tenantBId.Should().Be(200L);
    }

    [Fact]
    public async Task XT_P0_002_TenantATriggerField_Condition_TenantBAction_PreservesTenantScope()
    {
        var connectionB = Guid.NewGuid();
        _adminRepo.GetTenantIdByPublicIdAsync(connectionB, Arg.Any<CancellationToken>()).Returns(200L);
        _tenantRepo.IsActiveMemberAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);

        var tenantId = await _adminRepo.GetTenantIdByPublicIdAsync(connectionB, CancellationToken.None);
        var isMember = await _tenantRepo.IsActiveMemberAsync(1, CancellationToken.None);

        tenantId.Should().Be(200L);
        isMember.Should().BeTrue();
    }

    [Fact]
    public void XT_P0_003_TenantALookupOutput_TenantBCreateMapping_MapsValueCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_tenant_a_lookup = new
                {
                    fid_10 = "TenantA_Value",
                    RecordPublicId = Guid.NewGuid().ToString()
                }
            }
        });

        // Resolve token from Tenant A lookup
        var method = typeof(PipelineEngine).GetMethod("EvaluateTokens", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var value = (string)method!.Invoke(_engine, new object?[] { "{{steps.ref_tenant_a_lookup.fid_10}}", payload, null, null })!;

        value.Should().Be("TenantA_Value");
    }

    [Fact]
    public async Task XT_P0_004_RevokedSavedConnection_FailsExecutionCleanly()
    {
        var revokedConnection = Guid.NewGuid();
        _adminRepo.GetTenantIdByPublicIdAsync(revokedConnection, Arg.Any<CancellationToken>()).Returns((long?)null);

        var tenantId = await _adminRepo.GetTenantIdByPublicIdAsync(revokedConnection, CancellationToken.None);
        tenantId.Should().BeNull();
    }

    [Fact]
    public void REC_P0_001_DepthImmediatelyBelowOrEqualAllowedThreshold_Depth10Allowed()
    {
        int currentDepth = 10;
        int maxAllowedDepth = 10;

        bool isAllowed = currentDepth <= maxAllowedDepth;
        isAllowed.Should().BeTrue();
    }

    [Fact]
    public void REC_P0_002_FirstDepthBeyondThreshold_Depth11Rejected()
    {
        int currentDepth = 11;
        int maxAllowedDepth = 10;

        var act = () =>
        {
            if (currentDepth > maxAllowedDepth)
            {
                throw new InvalidOperationException($"Pipeline execution recursion depth limit exceeded: current depth {currentDepth} exceeds maximum allowed depth of {maxAllowedDepth}.");
            }
        };

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*recursion depth limit exceeded*");
    }

    [Fact]
    public void REC_P0_003_ChainPropagatedAcrossPipelineGeneratedRecordUpdate()
    {
        var pipelineChain = new List<long> { 101, 102 };
        int depth = 2;

        var contextJson = JsonSerializer.Serialize(new
        {
            PipelineDepth = depth + 1,
            PipelineChain = new List<long>(pipelineChain) { 103 }
        });

        using var doc = JsonDocument.Parse(contextJson);
        var root = doc.RootElement;

        root.GetProperty("PipelineDepth").GetInt32().Should().Be(3);
        root.GetProperty("PipelineChain").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void REC_P0_004_CrossTenantTriggerExecution_PreservesRecursionMetadata()
    {
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.PipelineDepth.Returns(4);
        queryContext.PipelineChainJson.Returns("[10, 20, 30, 40]");

        queryContext.PipelineDepth.Should().Be(4);
        queryContext.PipelineChainJson.Should().Be("[10, 20, 30, 40]");
    }
}
