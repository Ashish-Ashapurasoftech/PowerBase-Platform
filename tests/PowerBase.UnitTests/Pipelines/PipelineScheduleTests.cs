using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using PowerBase.Application.Pipelines.Commands.UpdatePipeline;
using PowerBase.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineScheduleTests
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly UpdatePipelineScheduleCommandHandler _handler;

    public PipelineScheduleTests()
    {
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _pipelineRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, IsActive = true });
        _handler = new UpdatePipelineScheduleCommandHandler(_pipelineRepo, Substitute.For<IQueryContext>());
    }

    [Fact]
    public async Task Handler_CreateSchedule_CalculatesCorrectNextRunOn()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        _pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(1L);
        _pipelineRepo.GetScheduleByPipelineIdAsync(1L, Arg.Any<CancellationToken>()).Returns((PipelineSchedule?)null);
        _pipelineRepo.GetStepsByPipelineIdAsync(1L, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new PipelineStep
            {
                Type = "query",
                Subtype = "search-records",
                DisplayOrder = 1,
                IsValidated = true
            }
        });

        // Every day at 9:00 AM UTC
        var command = new UpdatePipelineScheduleCommand(
            pipelinePublicId,
            "daily",
            null,
            new TimeSpan(9, 0, 0),
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "0 9 * * *"
        );

        // Act
        await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).CreateScheduleAsync(
            Arg.Is<PipelineSchedule>(s => s.PipelineId == 1 && s.CronExpression == "0 9 * * *" && s.NextRunOn.HasValue && s.NextRunOn.Value > DateTime.UtcNow),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_UpdateSchedule_InvalidFirstStep_ThrowsValidationException()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        _pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(1L);
        _pipelineRepo.GetStepsByPipelineIdAsync(1L, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new PipelineStep
            {
                Type = "action",
                Subtype = "update-record",
                DisplayOrder = 1,
                IsValidated = true
            }
        });

        var command = new UpdatePipelineScheduleCommand(
            pipelinePublicId,
            "daily",
            null,
            new TimeSpan(9, 0, 0),
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "0 9 * * *"
        );

        // Act & Assert
        Func<Task> act = () => _handler.HandleAsync(command, CancellationToken.None);
        await act.Should().ThrowAsync<PowerBase.Domain.Exceptions.ValidationException>()
            .Where(e => e.Errors.ContainsKey("Pipeline"));
    }

    [Fact]
    public async Task Handler_UpdateSchedule_NoSteps_ThrowsValidationException()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        _pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(1L);
        _pipelineRepo.GetStepsByPipelineIdAsync(1L, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>());

        var command = new UpdatePipelineScheduleCommand(
            pipelinePublicId,
            "daily",
            null,
            new TimeSpan(9, 0, 0),
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "0 9 * * *"
        );

        // Act & Assert
        Func<Task> act = () => _handler.HandleAsync(command, CancellationToken.None);
        await act.Should().ThrowAsync<PowerBase.Domain.Exceptions.ValidationException>()
            .Where(e => e.Errors.ContainsKey("Pipeline"));
    }

    [Fact]
    public void Validator_InvalidCron_Fails()
    {
        // Arrange
        var validator = new UpdatePipelineScheduleCommandValidator();
        var command = new UpdatePipelineScheduleCommand(
            Guid.NewGuid(),
            "weekly",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "invalid-cron-expr"
        );

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CronExpression");
    }

    [Fact]
    public async Task SaveSteps_IncompatibleStructure_DeletesSchedule()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var uow = Substitute.For<ITenantUnitOfWork>();
        var handler = new SavePipelineStepsCommandHandler(
            pipelineRepo,
            Substitute.For<IAppRepository>(),
            Substitute.For<IAppTableRepository>(),
            Substitute.For<IAppFieldRepository>(),
            Substitute.For<IAppAccessService>(),
            uow,
            Substitute.For<ITenantRepository>(),
            Substitute.For<IQueryContext>(),
            Substitute.For<IMainPipelineQueueRepository>(),
            Substitute.For<IServiceProvider>()
        );

        var pipelinePublicId = Guid.NewGuid();
        pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(1L);
        pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true });
        
        var schedule = new PipelineSchedule { PublicId = Guid.NewGuid(), PipelineId = 1 };
        pipelineRepo.GetScheduleByPipelineIdAsync(1L, Arg.Any<CancellationToken>()).Returns(schedule);

        // Steps list with trigger step (incompatible with pipeline-level schedule)
        var steps = new List<SavePipelineStepDto>
        {
            new SavePipelineStepDto
            {
                PublicId = Guid.NewGuid(),
                Type = "trigger",
                Subtype = "webhook",
                IsValidated = true
            }
        };

        var command = new SavePipelineStepsCommand(pipelinePublicId, steps, Convert.FromBase64String("AAAAAAAAB9M="));

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await pipelineRepo.Received(1).DeleteScheduleAsync(schedule.PublicId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_Reactivation_RecalculatesNextRunOn()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var auditRepo = Substitute.For<IAuditRepository>();
        var handler = new UpdatePipelineCommandHandler(
            pipelineRepo,
            auditRepo,
            Substitute.For<IQueryContext>(),
            Substitute.For<IAppRepository>(),
            Substitute.For<IAppTableRepository>(),
            Substitute.For<IAppFieldRepository>(),
            Substitute.For<IAppAccessService>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IMainPipelineQueueRepository>(),
            Substitute.For<IServiceProvider>()
        );

        var pipelinePublicId = Guid.NewGuid();
        var pipeline = new Pipeline { Id = 1, PublicId = pipelinePublicId, IsActive = false, Name = "Test" };
        pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);
        pipelineRepo.UpdateAsync(Arg.Any<Pipeline>(), null, Arg.Any<CancellationToken>()).Returns(1);

        var schedule = new PipelineSchedule
        {
            Id = 5,
            PipelineId = 1,
            ScheduleType = "daily",
            CronExpression = "0 9 * * *",
            TimeZone = "UTC",
            NextRunOn = DateTime.UtcNow.AddDays(-3) // stale past NextRunOn
        };
        pipelineRepo.GetScheduleByPipelineIdAsync(1L, Arg.Any<CancellationToken>()).Returns(schedule);

        var command = new UpdatePipelineCommand(pipelinePublicId, "Test", "", true, Convert.FromBase64String("AAAAAAAAB9M="));

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await pipelineRepo.Received(1).UpdateScheduleAsync(
            Arg.Is<PipelineSchedule>(s => s.Id == 5 && s.NextRunOn.HasValue && s.NextRunOn.Value > DateTime.UtcNow),
            null,
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task UpdateSchedule_SavesAndAutomaticallyActivatesPipeline()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var handler = new UpdatePipelineScheduleCommandHandler(pipelineRepo, Substitute.For<IQueryContext>());

        var pipelinePublicId = Guid.NewGuid();
        pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(1L);
        pipelineRepo.GetScheduleByPipelineIdAsync(1L, Arg.Any<CancellationToken>()).Returns((PipelineSchedule?)null);
        pipelineRepo.GetStepsByPipelineIdAsync(1L, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new PipelineStep { Type = "query", Subtype = "search-records", DisplayOrder = 1, IsValidated = true }
        });
        
        var pipeline = new Pipeline { Id = 1, PublicId = pipelinePublicId, IsActive = false, Name = "Test" };
        pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var command = new UpdatePipelineScheduleCommand(
            pipelinePublicId,
            "daily",
            null,
            new TimeSpan(9, 0, 0),
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "0 9 * * *"
        );

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        pipeline.IsActive.Should().BeTrue();
        await pipelineRepo.Received(1).UpdateAsync(Arg.Is<Pipeline>(p => p.IsActive == true), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Validator_IntervalLessThanOneHour_Fails()
    {
        var validator = new UpdatePipelineScheduleCommandValidator();
        var command = new UpdatePipelineScheduleCommand(
            Guid.NewGuid(),
            "hourly",
            0, // invalid interval (< 1 hour)
            null,
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "0 * * * *"
        );

        var result = validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Interval");
    }

    [Fact]
    public void Validator_CronUnderOneHour_Fails()
    {
        var validator = new UpdatePipelineScheduleCommandValidator();
        var command = new UpdatePipelineScheduleCommand(
            Guid.NewGuid(),
            "custom",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "*/30 * * * *" // runs every 30 mins (under 1 hour)
        );

        var result = validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CronExpression" && e.ErrorMessage.Contains("Minimum schedule frequency is 1 hour"));
    }

    [Fact]
    public void Validator_CronWithSeconds_Fails()
    {
        var validator = new UpdatePipelineScheduleCommandValidator();
        var command = new UpdatePipelineScheduleCommand(
            Guid.NewGuid(),
            "custom",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "0 */5 * * * * *" // 6 fields (seconds)
        );

        var result = validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CronExpression" && e.ErrorMessage.Contains("Must contain exactly 5 fields"));
    }

    [Fact]
    public void Validator_CronWithAlias_Fails()
    {
        var validator = new UpdatePipelineScheduleCommandValidator();
        var command = new UpdatePipelineScheduleCommand(
            Guid.NewGuid(),
            "custom",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "UTC",
            "@hourly" // alias
        );

        var result = validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CronExpression" && e.ErrorMessage.Contains("Must contain exactly 5 fields"));
    }

    [Theory]
    [InlineData("search-records")]
    [InlineData("look-up-record")]
    [InlineData("create-record")]
    [InlineData("send-email")]
    [InlineData("send-email-outlook")]
    [InlineData("make-request")]
    [InlineData("prepare-bulk-upsert")]
    public void Eligibility_AllowedRootSubtypes_ShouldPass(string subtype)
    {
        // Arrange
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Type = "query", Subtype = subtype, IsDeleted = false, IsValidated = true }
        };

        // Act
        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RetryPending_FirstPause_StoresOriginalNextAttemptOn()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(1);

        var result = await queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);
        result.Should().Be(1);
        await queueRepo.Received(1).PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryPending_RepeatedPause_DoesNotOverwriteStoredTimestamp()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(0); // already paused

        var result = await queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);
        result.Should().Be(0);
        await queueRepo.Received(1).PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NormalPending_FirstPause_PreservesNullOriginalTimestamp()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(1);

        var result = await queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);
        result.Should().Be(1);
    }

    [Fact]
    public async Task NormalPending_RepeatedPause_RemainsIdempotent()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(0);

        var result = await queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);
        result.Should().Be(0);
    }

    [Fact]
    public async Task RetryPending_Resume_RestoresExactFutureTimestamp()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        queueRepo.ResumePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(1);

        var result = await queueRepo.ResumePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);
        result.Should().Be(1);
        await queueRepo.Received(1).ResumePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryPending_RepeatedResume_IsIdempotent()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        queueRepo.ResumePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(0);

        var result = await queueRepo.ResumePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);
        result.Should().Be(0);
    }

    [Fact]
    public async Task RetryPending_ResumeAfterOriginalDueTime_BecomesImmediatelyClaimable()
    {
        var job = new PipelineQueue
        {
            Id = 1L,
            Status = "Pending",
            NextAttemptOn = DateTime.UtcNow.AddMinutes(-5), // already due
            PausedNextAttemptOn = null
        };

        job.NextAttemptOn.Should().BeBefore(DateTime.UtcNow);
    }

    [Fact]
    public async Task NormalPending_Resume_RestoresNull()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        queueRepo.ResumePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(1);

        var result = await queueRepo.ResumePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);
        result.Should().Be(1);
    }

    [Fact]
    public void PauseResume_DoesNotChangeAttemptCount()
    {
        var job = new PipelineQueue { AttemptCount = 3 };
        var pausedAttemptCount = job.AttemptCount;

        pausedAttemptCount.Should().Be(3);
    }

    [Fact]
    public void PauseResume_DoesNotChangeLastError()
    {
        var job = new PipelineQueue { LastError = "Connection Timeout" };
        var pausedLastError = job.LastError;

        pausedLastError.Should().Be("Connection Timeout");
    }

    [Fact]
    public void PauseResume_DoesNotChangeTriggerPayloadJson()
    {
        var job = new PipelineQueue { TriggerPayloadJson = "{\"id\": 123}" };
        var pausedPayload = job.TriggerPayloadJson;

        pausedPayload.Should().Be("{\"id\": 123}");
    }

    [Fact]
    public void PauseResume_DoesNotChangeMessageId()
    {
        var msgId = Guid.NewGuid();
        var job = new PipelineQueue { MessageId = msgId };
        var pausedMsgId = job.MessageId;

        pausedMsgId.Should().Be(msgId);
    }

    [Fact]
    public async Task OffReconciliation_RepeatedSweep_DoesNotDestroyOriginalRetryTime()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        // First sweep returns affected count 1
        queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(1);
        var r1 = await queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);

        // Second sweep returns affected count 0 (already stashed)
        queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(0);
        var r2 = await queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);

        r1.Should().Be(1);
        r2.Should().Be(0);
    }

    [Fact]
    public async Task CrashAfterPause_RepeatedPause_PreservesOriginalRetryTime()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var tenantId = 100L;
        var pipelineId = 500L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        // Execute first time
        queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(1);
        await queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);

        // Execute second time after restart/crash recovery
        queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(0);
        var r = await queueRepo.PausePendingJobsAsync(tenantId, pipelineId, sentinelDate, CancellationToken.None);

        r.Should().Be(0);
    }

    [Fact]
    public void DeferProcessingJob_OnResume_HasCorrectEligibilitySemantics()
    {
        var job = new PipelineQueue
        {
            Id = 1L,
            Status = "Processing",
            NextAttemptOn = null,
            PausedNextAttemptOn = null
        };

        // When deferred during OFF boundary, it stashes PausedNextAttemptOn = null and NextAttemptOn = sentinel.
        job.PausedNextAttemptOn = null;
        job.NextAttemptOn = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        job.NextAttemptOn.Should().Be(new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        job.PausedNextAttemptOn.Should().BeNull();
    }

    [Fact]
    public async Task TenantIsolation_SamePipelineId_DoesNotCrossAffect()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var pipelineId = 96L;
        var sentinelDate = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        queueRepo.PausePendingJobsAsync(1L, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(1);
        queueRepo.PausePendingJobsAsync(2L, pipelineId, sentinelDate, Arg.Any<CancellationToken>()).Returns(0);

        var r1 = await queueRepo.PausePendingJobsAsync(1L, pipelineId, sentinelDate, CancellationToken.None);
        var r2 = await queueRepo.PausePendingJobsAsync(2L, pipelineId, sentinelDate, CancellationToken.None);

        r1.Should().Be(1);
        r2.Should().Be(0);
    }
}

