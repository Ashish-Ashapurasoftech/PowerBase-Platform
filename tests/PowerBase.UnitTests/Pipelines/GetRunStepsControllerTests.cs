using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.API.Controllers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Queries.GetPipelineRunSteps;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class GetRunStepsControllerTests
{
    private readonly IAppAccessService _accessService;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly GetPipelineRunStepsQueryHandler _queryHandler;
    private readonly PipelinesController _controller;

    public GetRunStepsControllerTests()
    {
        _accessService = Substitute.For<IAppAccessService>();
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _queryHandler = new GetPipelineRunStepsQueryHandler(_pipelineRepo);

        // Pass null! for all unused constructor parameters of the controller in these action-level tests
        _controller = new PipelinesController(null!, null!, null!, null!, null!, null!, null!);

        var httpContext = new DefaultHttpContext();
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    [Fact]
    public async Task GetRunSteps_AuthorizedReader_ReturnsOk()
    {
        // Arrange
        var runPubId = Guid.NewGuid();
        var pipelinePubId = Guid.NewGuid();
        var run = new PipelineRun { Id = 10, PipelineId = 20, PublicId = runPubId };
        var pipeline = new Pipeline { Id = 20, PublicId = pipelinePubId };

        _pipelineRepo.GetRunByPublicIdAsync(runPubId, Arg.Any<CancellationToken>()).Returns(run);
        _pipelineRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(pipeline);
        _pipelineRepo.GetStepsByPipelineIdAsync(20, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>());
        _pipelineRepo.GetStepRunsByRunIdAsync(10, Arg.Any<CancellationToken>()).Returns(new List<PipelineStepRun>());

        // Act
        var result = await _controller.GetRunSteps(runPubId, _queryHandler, _accessService, _pipelineRepo);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        await _accessService.Received(1).RequirePermissionByPipelinePublicIdAsync(pipelinePubId, "PowerFlows:read", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRunSteps_NonExistentRun_ReturnsNotFound()
    {
        // Arrange
        var runPubId = Guid.NewGuid();
        _pipelineRepo.GetRunByPublicIdAsync(runPubId, Arg.Any<CancellationToken>()).Returns((PipelineRun?)null);

        // Act
        var result = await _controller.GetRunSteps(runPubId, _queryHandler, _accessService, _pipelineRepo);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetRunSteps_UnauthorizedUser_ReturnsForbidden()
    {
        // Arrange
        var runPubId = Guid.NewGuid();
        var pipelinePubId = Guid.NewGuid();
        var run = new PipelineRun { Id = 10, PipelineId = 20, PublicId = runPubId };
        var pipeline = new Pipeline { Id = 20, PublicId = pipelinePubId };

        _pipelineRepo.GetRunByPublicIdAsync(runPubId, Arg.Any<CancellationToken>()).Returns(run);
        _pipelineRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(pipeline);

        _accessService.RequirePermissionByPipelinePublicIdAsync(pipelinePubId, "PowerFlows:read", Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedActionException("You do not have permission."));

        // Act
        var result = await _controller.GetRunSteps(runPubId, _queryHandler, _accessService, _pipelineRepo);

        // Assert
        result.Should().BeOfType<ObjectResult>();
        var objectResult = (ObjectResult)result;
        objectResult.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Handler_UsesExecutionTimeStepSnapshot_AndReturnsDiagnostics()
    {
        var runPubId = Guid.NewGuid();
        var originalStepPublicId = Guid.NewGuid();
        _pipelineRepo.GetRunByPublicIdAsync(runPubId, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 10, PipelineId = 20, PublicId = runPubId });
        _pipelineRepo.GetStepsByPipelineIdAsync(20, Arg.Any<CancellationToken>()).Returns(new List<PipelineStep>
        {
            new() { Id = 30, PublicId = Guid.NewGuid(), RefId = "renamed", Label = "New name", Type = "action", Subtype = "update-record" }
        });
        _pipelineRepo.CountStepRunsByRunIdAsync(10, Arg.Any<CancellationToken>()).Returns(1);
        _pipelineRepo.GetStepRunsByRunIdAsync(10, 1, 50, Arg.Any<CancellationToken>()).Returns(new List<PipelineStepRun>
        {
            new()
            {
                Id = 40,
                PipelineRunId = 10,
                StepId = 30,
                Status = "Success",
                StartedOn = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc),
                CompletedOn = new DateTime(2026, 9, 24, 10, 0, 0, 125, DateTimeKind.Utc),
                StepPublicIdSnapshot = originalStepPublicId,
                StepRefIdSnapshot = "a",
                StepLabelSnapshot = "Create customer",
                StepTypeSnapshot = "action",
                StepSubtypeSnapshot = "create-record",
                PipelineRunAttemptId = 7,
                ExecutionPath = "root/a",
                SequenceNumber = 1,
                TransactionOutcome = "Committed"
            }
        });

        var result = await _queryHandler.HandleAsync(new GetPipelineRunStepsQuery(runPubId));

        var item = result.Items.Should().ContainSingle().Subject;
        item.StepPublicId.Should().Be(originalStepPublicId);
        item.StepRefId.Should().Be("a");
        item.StepLabel.Should().Be("Create customer");
        item.StepSubtype.Should().Be("create-record");
        item.PipelineRunAttemptId.Should().Be(7);
        item.ExecutionPath.Should().Be("root/a");
        item.SequenceNumber.Should().Be(1);
        item.TransactionOutcome.Should().Be("Committed");
        item.DurationMs.Should().Be(125);
    }
}
