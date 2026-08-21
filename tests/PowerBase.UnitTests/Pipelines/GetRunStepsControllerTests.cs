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
}
