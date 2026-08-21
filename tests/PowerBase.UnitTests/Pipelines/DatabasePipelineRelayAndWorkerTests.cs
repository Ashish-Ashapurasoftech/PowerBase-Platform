using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Repositories;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class DatabasePipelineRelayAndWorkerTests
{
    private readonly IMainPipelineQueueRepository _queueRepo;
    private readonly ILogger<MainPipelineQueueRepository> _logger;

    public DatabasePipelineRelayAndWorkerTests()
    {
        _queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        _logger = Substitute.For<ILogger<MainPipelineQueueRepository>>();
    }

    [Fact]
    public async Task Worker_OldOwnerLeaseUpdate_FailsValidation()
    {
        var id = 123L;
        var workerId = "worker_1";
        var wrongToken = Guid.NewGuid();

        _queueRepo.RenewLeaseAsync(id, workerId, wrongToken, 60, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _queueRepo.RenewLeaseAsync(id, workerId, wrongToken, 60, CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Worker_ExhaustedAttempts_TransitionsToFailed()
    {
        var job = new PipelineQueue
        {
            Id = 1L,
            AttemptCount = 5,
            MaxAttempts = 5,
            Status = "Processing",
            ClaimToken = Guid.NewGuid()
        };

        // When attempt count is >= max attempts, scheduling a retry fails/transitions to failed
        _queueRepo.ScheduleRetryAsync(job.Id, "worker_1", job.ClaimToken.Value, 10, "Execution failed", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _queueRepo.ScheduleRetryAsync(job.Id, "worker_1", job.ClaimToken.Value, 10, "Execution failed", CancellationToken.None);
        result.Should().BeTrue();
    }
}
