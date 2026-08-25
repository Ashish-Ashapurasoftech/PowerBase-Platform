using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class SavePipelineStepsCommandHandlerTests
{
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly ITenantUnitOfWork _uow = Substitute.For<ITenantUnitOfWork>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly SavePipelineStepsCommandHandler _handler;

    public SavePipelineStepsCommandHandlerTests()
    {
        _handler = new SavePipelineStepsCommandHandler(_pipelineRepo, _appRepo, _tableRepo, _fieldRepo, _appAccessService, _uow, _tenantRepo, _queryContext, Substitute.For<IServiceProvider>());
    }

    [Fact]
    public async Task HandleAsync_ValidHierarchy_ShouldFlattenAndPreserveBranchNames()
    {
        // Arrange
        var pipelineId = 123L;
        var pipelinePublicId = Guid.NewGuid();
        var rowVersion = new byte[] { 1, 2, 3, 4 };

        _pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(pipelineId);

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, IsActive = false });

        var triggerStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "trigger_1",
            Type = "trigger",
            Subtype = "record-added",
            IsValidated = true
        };

        var conditionStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "condition_1",
            Type = "condition",
            Subtype = "branch",
            IsValidated = true,
            Children = new List<SavePipelineStepDto>
            {
                new() { PublicId = Guid.NewGuid(), RefId = "action_then", Type = "action", Subtype = "send-email", IsValidated = true }
            },
            ElseChildren = new List<SavePipelineStepDto>
            {
                new() { PublicId = Guid.NewGuid(), RefId = "action_else", Type = "action", Subtype = "stop", IsValidated = true }
            }
        };

        var command = new SavePipelineStepsCommand(
            pipelinePublicId,
            new List<SavePipelineStepDto> { triggerStep, conditionStep },
            rowVersion
        );

        IEnumerable<PipelineStep> capturedSteps = null!;
        await _pipelineRepo.SaveStepsAsync(
            pipelineId,
            Arg.Do<IEnumerable<PipelineStep>>(steps => capturedSteps = steps),
            rowVersion,
            Arg.Any<bool>(),
            _uow.Transaction,
            Arg.Any<CancellationToken>()
        );

        // Act
        await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        capturedSteps.Should().NotBeNull();
        var stepsList = capturedSteps.ToList();
        stepsList.Should().HaveCount(4); // trigger + condition + then action + else action

        // Trigger step checks (root)
        var trigger = stepsList.Single(s => s.RefId == "trigger_1");
        trigger.ParentPublicId.Should().BeNull();
        trigger.ParentBranch.Should().BeNull();

        // Condition step checks (root)
        var condition = stepsList.Single(s => s.RefId == "condition_1");
        condition.ParentPublicId.Should().BeNull();
        condition.ParentBranch.Should().BeNull();

        // Then branch step checks
        var thenAction = stepsList.Single(s => s.RefId == "action_then");
        thenAction.ParentPublicId.Should().Be(condition.PublicId);
        thenAction.ParentBranch.Should().Be("children");

        // Else branch step checks
        var elseAction = stepsList.Single(s => s.RefId == "action_else");
        elseAction.ParentPublicId.Should().Be(condition.PublicId);
        elseAction.ParentBranch.Should().Be("elseChildren");

        // Transaction check
        await _uow.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ActivePipelineWithIncompleteStep_ThrowsValidationException()
    {
        // Arrange
        var pipelineId = 123L;
        var pipelinePublicId = Guid.NewGuid();
        var rowVersion = new byte[] { 1, 2, 3, 4 };

        _pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(pipelineId);

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, IsActive = true });

        var triggerStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "trigger_1",
            Type = "trigger",
            Subtype = "record-added",
            IsValidated = true
        };

        var incompleteStep = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "search_1",
            Type = "query",
            Subtype = "search-records",
            IsValidated = false // Incomplete!
        };

        var command = new SavePipelineStepsCommand(
            pipelinePublicId,
            new List<SavePipelineStepDto> { triggerStep, incompleteStep },
            rowVersion
        );

        // Act
        Func<Task> act = async () => await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        var exception = await act.Should().ThrowAsync<PowerBase.Domain.Exceptions.ValidationException>();
        exception.Which.Errors.Should().ContainKey("Steps");
        exception.Which.Errors["Steps"].Should().Contain("Cannot save incomplete steps to an Active PowerFlow.");
    }

    [Fact]
    public async Task HandleAsync_StepsChangedOnActivePipeline_ShouldDeactivatePipeline()
    {
        // Arrange
        var pipelineId = 123L;
        var pipelinePublicId = Guid.NewGuid();
        var rowVersion = new byte[] { 1, 2, 3, 4 };

        _pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(pipelineId);

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, IsActive = true });

        // Database steps (original setup)
        var dbSteps = new List<PipelineStep>
        {
            new() { Id = 1, PublicId = Guid.NewGuid(), RefId = "trigger_1", Type = "trigger", Subtype = "record-added", IsValidated = true, IsDeleted = false }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>())
            .Returns(dbSteps);

        // Incoming steps (modified) - new action step added
        var triggerStepDto = new SavePipelineStepDto
        {
            PublicId = dbSteps[0].PublicId,
            RefId = "trigger_1",
            Type = "trigger",
            Subtype = "record-added",
            IsValidated = true
        };

        var actionStepDto = new SavePipelineStepDto
        {
            PublicId = Guid.NewGuid(),
            RefId = "action_1",
            Type = "action",
            Subtype = "send-email",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            pipelinePublicId,
            new List<SavePipelineStepDto> { triggerStepDto, actionStepDto },
            rowVersion
        );

        bool capturedDeactivateValue = false;
        await _pipelineRepo.SaveStepsAsync(
            pipelineId,
            Arg.Any<IEnumerable<PipelineStep>>(),
            rowVersion,
            Arg.Do<bool>(val => capturedDeactivateValue = val),
            _uow.Transaction,
            Arg.Any<CancellationToken>()
        );

        // Act
        await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        capturedDeactivateValue.Should().BeTrue();
        await _uow.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoOpStepsSaveOnActivePipeline_ShouldNotDeactivatePipeline()
    {
        // Arrange
        var pipelineId = 123L;
        var pipelinePublicId = Guid.NewGuid();
        var rowVersion = new byte[] { 1, 2, 3, 4 };

        _pipelineRepo.GetIdByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(pipelineId);

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = pipelineId, PublicId = pipelinePublicId, IsActive = true });

        // Database steps
        var dbSteps = new List<PipelineStep>
        {
            new() { Id = 1, PublicId = Guid.NewGuid(), RefId = "trigger_1", Type = "trigger", Subtype = "record-added", ConfigJson = "{}", IsValidated = true, IsDeleted = false }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(pipelineId, Arg.Any<CancellationToken>())
            .Returns(dbSteps);

        // Incoming steps (identical)
        var triggerStepDto = new SavePipelineStepDto
        {
            PublicId = dbSteps[0].PublicId,
            RefId = "trigger_1",
            Type = "trigger",
            Subtype = "record-added",
            ConfigJson = "{}",
            IsValidated = true
        };

        var command = new SavePipelineStepsCommand(
            pipelinePublicId,
            new List<SavePipelineStepDto> { triggerStepDto },
            rowVersion
        );

        bool capturedDeactivateValue = true;
        await _pipelineRepo.SaveStepsAsync(
            pipelineId,
            Arg.Any<IEnumerable<PipelineStep>>(),
            rowVersion,
            Arg.Do<bool>(val => capturedDeactivateValue = val),
            _uow.Transaction,
            Arg.Any<CancellationToken>()
        );

        // Act
        await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        capturedDeactivateValue.Should().BeFalse();
    }
}
