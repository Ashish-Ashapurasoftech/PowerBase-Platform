using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Commands.DeletePipelines;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class DeletePipelinesCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private readonly Guid _appPublicId = Guid.NewGuid();
    private readonly long _appId = 1L;

    public DeletePipelinesCommandHandlerTests()
    {
        _appRepo.GetIdByPublicIdAsync(_appPublicId, Arg.Any<CancellationToken>()).Returns(_appId);
    }

    [Fact]
    public async Task HandleAsync_EmptyRequest_ThrowsValidationException()
    {
        // Arrange
        var command = new DeletePipelinesCommand(_appPublicId, new List<Guid>());
        var handler = new DeletePipelinesCommandHandler(_appRepo, _pipelineRepo, _auditRepo);

        // Act & Assert
        await handler.Invoking(h => h.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("pipelinePublicIds"));
    }

    [Fact]
    public async Task HandleAsync_GuidEmpty_ThrowsValidationException()
    {
        // Arrange
        var command = new DeletePipelinesCommand(_appPublicId, new List<Guid> { Guid.NewGuid(), Guid.Empty });
        var handler = new DeletePipelinesCommandHandler(_appRepo, _pipelineRepo, _auditRepo);

        // Act & Assert
        await handler.Invoking(h => h.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.ContainsKey("pipelinePublicIds"));
    }

    [Fact]
    public async Task HandleAsync_CrossAppPipeline_ThrowsUnauthorizedActionException()
    {
        // Arrange
        var pipelineId1 = Guid.NewGuid();
        var pipeline1 = new Pipeline { PublicId = pipelineId1, AppId = _appId, Name = "Pipeline 1" };
        
        var pipelineId2 = Guid.NewGuid();
        var pipeline2 = new Pipeline { PublicId = pipelineId2, AppId = 999L, Name = "Cross App Pipeline" }; // Belongs to different App

        _pipelineRepo.GetByPublicIdAsync(pipelineId1, Arg.Any<CancellationToken>()).Returns(pipeline1);
        _pipelineRepo.GetByPublicIdAsync(pipelineId2, Arg.Any<CancellationToken>()).Returns(pipeline2);

        var command = new DeletePipelinesCommand(_appPublicId, new List<Guid> { pipelineId1, pipelineId2 });
        var handler = new DeletePipelinesCommandHandler(_appRepo, _pipelineRepo, _auditRepo);

        // Act & Assert
        await handler.Invoking(h => h.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedActionException>()
            .WithMessage("*do not belong to this application.*");

        // Verify no deletion occurred
        await _pipelineRepo.DidNotReceiveWithAnyArgs().SoftDeleteManyAsync(null!, default);
    }

    [Fact]
    public async Task HandleAsync_NonexistentPipeline_ThrowsNotFoundExceptionAndAborts()
    {
        // Arrange
        var pipelineId1 = Guid.NewGuid();
        var pipeline1 = new Pipeline { PublicId = pipelineId1, AppId = _appId, Name = "Pipeline 1" };
        var nonexistentId = Guid.NewGuid();

        _pipelineRepo.GetByPublicIdAsync(pipelineId1, Arg.Any<CancellationToken>()).Returns(pipeline1);
        _pipelineRepo.GetByPublicIdAsync(nonexistentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Pipeline>(new NotFoundException("PowerFlow", nonexistentId)));

        var command = new DeletePipelinesCommand(_appPublicId, new List<Guid> { pipelineId1, nonexistentId });
        var handler = new DeletePipelinesCommandHandler(_appRepo, _pipelineRepo, _auditRepo);

        // Act & Assert
        await handler.Invoking(h => h.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();

        // Verify no deletion occurred
        await _pipelineRepo.DidNotReceiveWithAnyArgs().SoftDeleteManyAsync(null!, default);
    }

    [Fact]
    public async Task HandleAsync_DuplicateIdsNormalized_DeletesDistinctAndLogsAudits()
    {
        // Arrange
        var pipelineId1 = Guid.NewGuid();
        var pipeline1 = new Pipeline { PublicId = pipelineId1, AppId = _appId, Name = "Pipeline 1" };

        _pipelineRepo.GetByPublicIdAsync(pipelineId1, Arg.Any<CancellationToken>()).Returns(pipeline1);

        // Pass duplicate IDs
        var command = new DeletePipelinesCommand(_appPublicId, new List<Guid> { pipelineId1, pipelineId1 });
        var handler = new DeletePipelinesCommandHandler(_appRepo, _pipelineRepo, _auditRepo);

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert: SoftDeleteManyAsync was called with only distinct IDs
        await _pipelineRepo.Received(1).SoftDeleteManyAsync(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Count() == 1 && ids.Contains(pipelineId1)),
            Arg.Any<CancellationToken>());

        // Assert: LogActivityAsync was called exactly once for the distinct pipeline
        await _auditRepo.Received(1).LogActivityAsync(
            AuditActions.Deleted,
            AuditEntityTypes.Pipeline,
            pipelineId1.ToString(),
            "Pipeline workflow deleted: Pipeline 1",
            appId: _appId,
            ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ValidActiveAndDraftPipelines_Succeeds()
    {
        // Arrange
        var activeId = Guid.NewGuid();
        var activePipeline = new Pipeline { PublicId = activeId, AppId = _appId, Name = "Active Flow", IsActive = true };

        var draftId = Guid.NewGuid();
        var draftPipeline = new Pipeline { PublicId = draftId, AppId = _appId, Name = "Draft Flow", IsActive = false };

        _pipelineRepo.GetByPublicIdAsync(activeId, Arg.Any<CancellationToken>()).Returns(activePipeline);
        _pipelineRepo.GetByPublicIdAsync(draftId, Arg.Any<CancellationToken>()).Returns(draftPipeline);

        var command = new DeletePipelinesCommand(_appPublicId, new List<Guid> { activeId, draftId });
        var handler = new DeletePipelinesCommandHandler(_appRepo, _pipelineRepo, _auditRepo);

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).SoftDeleteManyAsync(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Count() == 2 && ids.Contains(activeId) && ids.Contains(draftId)),
            Arg.Any<CancellationToken>());

        await _auditRepo.Received(1).LogActivityAsync(
            AuditActions.Deleted,
            AuditEntityTypes.Pipeline,
            activeId.ToString(),
            "Pipeline workflow deleted: Active Flow",
            appId: _appId,
            ct: Arg.Any<CancellationToken>());

        await _auditRepo.Received(1).LogActivityAsync(
            AuditActions.Deleted,
            AuditEntityTypes.Pipeline,
            draftId.ToString(),
            "Pipeline workflow deleted: Draft Flow",
            appId: _appId,
            ct: Arg.Any<CancellationToken>());
    }
}
