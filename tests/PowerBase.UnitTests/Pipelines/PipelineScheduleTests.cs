using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;
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
        _handler = new UpdatePipelineScheduleCommandHandler(_pipelineRepo);
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
                DisplayOrder = 1
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
                Subtype = "create-record",
                DisplayOrder = 1
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
}
