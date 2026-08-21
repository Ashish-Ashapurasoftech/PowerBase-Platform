using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Pipelines;
using PowerBase.Infrastructure.Persistence;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class DatabaseQueueTests
{
    private readonly IMainPipelineQueueRepository _queueRepo;
    private readonly IControlConnectionFactory _controlConnFactory;
    private readonly ITenantConnectionResolver _tenantResolver;
    private readonly ILogger<DatabasePipelineExecutionQueue> _queueLogger;

    public DatabaseQueueTests()
    {
        _queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        _controlConnFactory = Substitute.For<IControlConnectionFactory>();
        _tenantResolver = Substitute.For<ITenantConnectionResolver>();
        _queueLogger = Substitute.For<ILogger<DatabasePipelineExecutionQueue>>();
    }

    [Fact]
    public void OptionsValidator_ValidOptions_Passes()
    {
        var options = new PipelineExecutionOptions
        {
            PerInstanceTenantConcurrencyLimit = 5,
            DatabaseQueue = new DatabaseQueueOptions
            {
                LeaseSeconds = 60,
                MaxAttempts = 3
            }
        };

        Action act = () => PipelineExecutionOptionsValidator.Validate(options);
        act.Should().NotThrow();
    }

    [Fact]
    public void PayloadHashHelper_ComputesSha256()
    {
        var json = "{\"id\":123}";
        var hash = PayloadHashHelper.ComputeHash(json);
        hash.Should().NotBeNull();
        hash.Length.Should().Be(32);
    }

    [Fact]
    public void TenantPipelinePayloadMapper_MapsTriggerStepDetails()
    {
        var outbox = new PipelineOutboxItem
        {
            MessageId = Guid.NewGuid(),
            PipelineId = 1L,
            TriggerEvent = "record-added",
            TriggerPayloadJson = "{\"TriggerStepId\":456,\"TriggerStepRefId\":\"step_abc\"}",
            TriggeredBy = 99L,
            CorrelationId = Guid.NewGuid(),
            Depth = 2,
            PipelineChain = "[]",
            PayloadVersion = "1.0",
            CreatedOn = DateTime.UtcNow
        };

        var tenantPublicId = Guid.NewGuid();
        var pipelinePublicId = Guid.NewGuid();

        var job = TenantPipelinePayloadMapper.MapFromOutbox(outbox, 10L, tenantPublicId, pipelinePublicId);

        job.MessageId.Should().Be(outbox.MessageId);
        job.TenantId.Should().Be(10L);
        job.TenantPublicId.Should().Be(tenantPublicId);
        job.PipelinePublicId.Should().Be(pipelinePublicId);
        job.TriggerStepId.Should().Be(456L);
        job.TriggerStepRefId.Should().Be("step_abc");
        job.QueueSource.Should().Be("Event");
    }
}
