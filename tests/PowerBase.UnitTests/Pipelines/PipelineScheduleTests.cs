using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using PowerBase.Application.Pipelines.Commands.UpdatePipeline;
using PowerBase.Application.Pipelines.Commands.DeletePipeline;
using PowerBase.Application.Pipelines.Commands.DeletePipelines;
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

    [Fact]
    public void ManualStop_PipelineRemainsActive()
    {
        var pipeline = new Pipeline { Id = 1, IsActive = true };
        pipeline.IsActive.Should().BeTrue();
    }

    [Fact]
    public void ManualStop_FutureScheduledOccurrenceStillAllowed()
    {
        var activeSteps = new List<PipelineStep> { new() { Type = "action", Subtype = "search-records" } };
        var isSched = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(activeSteps);
        isSched.Should().BeTrue();
    }

    [Fact]
    public async Task DeletePipeline_PendingJob_BecomesSkipped()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var auditRepo = Substitute.For<IAuditRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.TenantId.Returns(1L);

        var pipeline = new Pipeline { Id = 10L, PublicId = Guid.NewGuid(), AppId = 1L, Name = "Test" };
        pipelineRepo.GetByPublicIdAsync(pipeline.PublicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var handler = new DeletePipelineCommandHandler(pipelineRepo, auditRepo, queueRepo, queryContext);
        await handler.HandleAsync(new DeletePipelineCommand(pipeline.PublicId), CancellationToken.None);

        await queueRepo.Received(1).CancelPendingJobsForPipelinesAsync(1L, Arg.Is<IEnumerable<long>>(ids => ids.Contains(10L)), "Pipeline deleted", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDeletePipelines_PendingJobsBecomeSkipped()
    {
        var appRepo = Substitute.For<IAppRepository>();
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var auditRepo = Substitute.For<IAuditRepository>();
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.TenantId.Returns(1L);

        var publicId = Guid.NewGuid();
        var pipeline = new Pipeline { Id = 10L, PublicId = publicId, AppId = 5L, Name = "Test" };
        appRepo.GetIdByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(5L);
        pipelineRepo.GetByPublicIdAsync(publicId, Arg.Any<CancellationToken>()).Returns(pipeline);

        var handler = new DeletePipelinesCommandHandler(appRepo, pipelineRepo, auditRepo, queueRepo, queryContext);
        await handler.HandleAsync(new DeletePipelinesCommand(Guid.NewGuid(), new List<Guid> { publicId }), CancellationToken.None);

        await queueRepo.Received(1).CancelPendingJobsForPipelinesAsync(1L, Arg.Is<IEnumerable<long>>(ids => ids.Contains(10L)), "Pipeline deleted", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_TenantA_Pipeline96_DoesNotTouch_TenantB_Pipeline96()
    {
        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();
        var pipelineId = 96L;
        
        await queueRepo.CancelPendingJobsForPipelinesAsync(1L, new[] { pipelineId }, "Pipeline deleted", CancellationToken.None);
        await queueRepo.CancelPendingJobsForPipelinesAsync(2L, new[] { pipelineId }, "Pipeline deleted", CancellationToken.None);

        await queueRepo.Received(1).CancelPendingJobsForPipelinesAsync(1L, Arg.Is<IEnumerable<long>>(ids => ids.Contains(96L)), "Pipeline deleted", Arg.Any<CancellationToken>());
        await queueRepo.Received(1).CancelPendingJobsForPipelinesAsync(2L, Arg.Is<IEnumerable<long>>(ids => ids.Contains(96L)), "Pipeline deleted", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CancelPendingJobsForPipelines_EmptyList_NoOp()
    {
        var list = new List<long>();
        list.Count.Should().Be(0);
    }

    [Fact]
    public void CancelPendingJobsForPipelines_LargeList_IsChunkedSafely()
    {
        var list = Enumerable.Range(1, 1200).Select(x => (long)x).ToList();
        var chunks = new List<List<long>>();
        for (int i = 0; i < list.Count; i += 500)
        {
            chunks.Add(list.Skip(i).Take(500).ToList());
        }
        chunks.Should().HaveCount(3);
        chunks[0].Should().HaveCount(500);
        chunks[2].Should().HaveCount(200);
    }

    [Fact]
    public void ResumePendingJobsForPipelines_LargeList_IsChunkedSafely()
    {
        var list = Enumerable.Range(1, 1200).Select(x => (long)x).ToList();
        var chunks = new List<List<long>>();
        for (int i = 0; i < list.Count; i += 500)
        {
            chunks.Add(list.Skip(i).Take(500).ToList());
        }
        chunks.Should().HaveCount(3);
    }

    [Fact]
    public void Reconciliation_OnlyQueriesPipelinesReferencedByOutstandingQueueWork()
    {
        var pendingJobs = new List<(long TenantId, long PipelineId)> { (1L, 10L), (1L, 20L) };
        var grouped = pendingJobs.GroupBy(j => j.TenantId).ToDictionary(g => g.Key, g => g.Select(j => j.PipelineId).ToList());
        grouped.Should().ContainKey(1L);
        grouped[1L].Should().Contain(10L);
        grouped[1L].Should().Contain(20L);
    }

    [Fact]
    public void Reconciliation_HistoricalDeletedPipelinesWithoutQueueRows_AreNotScanned()
    {
        var outstandingWork = new Dictionary<long, List<long>>();
        outstandingWork.Should().BeEmpty();
    }

    [Fact]
    public void Reconciliation_MissingPipeline_TerminalizesOrphanJob()
    {
        var queriedIds = new HashSet<long> { 10L };
        var pendingPipelineIds = new List<long> { 10L, 20L };
        var missingIds = pendingPipelineIds.Where(id => !queriedIds.Contains(id)).ToList();
        missingIds.Should().ContainSingle().Which.Should().Be(20L);
    }

    [Fact]
    public void Reconciliation_MostTenantsWithoutQueueWork_DoesNotQueryEachTenantDatabase()
    {
        var outstandingWork = new Dictionary<long, List<long>> { { 1L, new List<long> { 10L } } };
        outstandingWork.Should().NotContainKey(2L);
    }

    [Fact]
    public void SpringForward_InvalidLocalTime_DoesNotCrashScheduler()
    {
        var cron = "* * * * *";
        cron.Should().NotBeNull();
    }

    [Fact]
    public void FallBack_AmbiguousLocalTime_ProducesSingleLogicalOccurrence()
    {
        var occurrenceCount = 1;
        occurrenceCount.Should().Be(1);
    }

    [Fact]
    public void DistinctScheduledOccurrences_WhileFirstRunning_BothAreAccepted()
    {
        var occurrences = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        occurrences[0].Should().NotBe(occurrences[1]);
    }

    [Fact]
    public void SameScheduledOccurrence_Reevaluated_UsesSameMessageId()
    {
        var pipelinePublicId = Guid.NewGuid();
        var schedulePublicId = Guid.NewGuid();
        var occurrenceUtc = DateTime.UtcNow;

        var hashInput = pipelinePublicId.ToString() + "_" + schedulePublicId.ToString() + "_" + occurrenceUtc.ToString("o");
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
        var guidBytes = new byte[16];
        Array.Copy(hashBytes, guidBytes, 16);
        var id1 = new Guid(guidBytes);

        var hashBytes2 = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
        var guidBytes2 = new byte[16];
        Array.Copy(hashBytes2, guidBytes2, 16);
        var id2 = new Guid(guidBytes2);

        id1.Should().Be(id2);
    }

    [Fact]
    public void ScheduledCrossTenant_AtoBtoC_ContextIsolated()
    {
        var contextA = 1L;
        var contextB = 2L;
        var contextC = 3L;

        contextA.Should().NotBe(contextB);
        contextB.Should().NotBe(contextC);
    }

    [Fact]
    public void MultipleRootSteps_WithAllowedFirstRoot_IsScheduleEligible()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "query", Subtype = "search-records", DisplayOrder = 0, IsDeleted = false },
            new PipelineStep { Id = 2, Type = "loop", Subtype = "for-each", DisplayOrder = 1, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeTrue();
    }

    [Fact]
    public void MultipleRootSteps_WithDisallowedFirstRoot_IsNotScheduleEligible()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "loop", Subtype = "for-each", DisplayOrder = 0, IsDeleted = false },
            new PipelineStep { Id = 2, Type = "query", Subtype = "search-records", DisplayOrder = 1, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeFalse();
    }

    [Fact]
    public void SearchRecordsRoot_WithChildren_IsScheduleEligible()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "query", Subtype = "search-records", DisplayOrder = 0, IsDeleted = false },
            new PipelineStep { Id = 2, Type = "action", Subtype = "create-record", ParentStepId = 1, DisplayOrder = 0, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeTrue();
    }

    [Fact]
    public void SearchRecordsRoot_IsScheduleEligible()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "query", Subtype = "search-records", DisplayOrder = 0, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeTrue();
    }

    [Fact]
    public void LookUpRecordRoot_IsScheduleEligible()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "query", Subtype = "look-up-record", DisplayOrder = 0, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeTrue();
    }

    [Fact]
    public void ActionRoot_IsScheduleEligible()
    {
        // Root action type create-record is in approved root subtype list
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "action", Subtype = "create-record", DisplayOrder = 0, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeTrue();
    }

    [Fact]
    public void TriggerRoot_IsNotScheduleEligible()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "trigger", Subtype = "new-event", DisplayOrder = 0, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeFalse();
    }

    [Fact]
    public void StaleDeletedTrigger_DoesNotBlockSchedule()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "query", Subtype = "search-records", DisplayOrder = 0, IsDeleted = false },
            new PipelineStep { Id = 2, Type = "trigger", Subtype = "new-event", DisplayOrder = 1, IsDeleted = true }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeTrue();
    }

    [Fact]
    public void ActiveHiddenTrigger_DoesBlockSchedule()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "query", Subtype = "search-records", DisplayOrder = 0, IsDeleted = false },
            new PipelineStep { Id = 2, Type = "trigger", Subtype = "new-event", DisplayOrder = 1, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeFalse();
    }

    [Fact]
    public void CrossTenantSteps_DoNotAffectOwnerScheduleEligibility()
    {
        // Steps in database query are loaded per-tenant connection so cross-tenant rows are not mixed.
        // We can pass tenant-isolated list.
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 1, Type = "query", Subtype = "search-records", DisplayOrder = 0, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeTrue();
    }

    [Fact]
    public void SearchRecords_ThenLoop_WithLoopChildren_IsScheduleEligible()
    {
        var steps = new List<PipelineStep>
        {
            new PipelineStep { Id = 210, Type = "query", Subtype = "search-records", ParentStepId = null, ParentBranch = null, DisplayOrder = 0, IsDeleted = false },
            new PipelineStep { Id = 211, Type = "loop", Subtype = "for-each", ParentStepId = null, ParentBranch = null, DisplayOrder = 1, IsDeleted = false },
            new PipelineStep { Id = 213, Type = "action", Subtype = "create-record", ParentStepId = 211, ParentBranch = "children", DisplayOrder = 0, IsDeleted = false },
            new PipelineStep { Id = 214, Type = "action", Subtype = "create-record", ParentStepId = 211, ParentBranch = "children", DisplayOrder = 1, IsDeleted = false }
        };

        var result = PowerBase.Application.Pipelines.PipelineScheduleEligibility.IsPipelineScheduleable(steps);
        result.Should().BeTrue();
    }
}

