using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Commands.CreatePipeline;
using PowerBase.Application.Pipelines.Commands.UpdatePipeline;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    public PipelineCommandHandlerTests()
    {
        _queryContext.UserId.Returns(100L);
    }

    [Fact]
    public async Task CreatePipeline_DuplicateName_ThrowsDuplicateException()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var appId = 1L;
        var command = new CreatePipelineCommand(appPublicId, "DuplicatePipeline", "Some desc");
        
        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _pipelineRepo.NameExistsForUserAsync(100L, "DuplicatePipeline", Arg.Any<CancellationToken>()).Returns(true);

        var handler = new CreatePipelineCommandHandler(_appRepo, _pipelineRepo, _auditRepo, _queryContext);

        // Act & Assert
        await handler.Invoking(h => h.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task CreatePipeline_UniqueName_Succeeds()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var appId = 1L;
        var command = new CreatePipelineCommand(appPublicId, "UniquePipeline", "Some desc");
        
        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _pipelineRepo.NameExistsForUserAsync(100L, "UniquePipeline", Arg.Any<CancellationToken>()).Returns(false);
        _pipelineRepo.CreateAsync(Arg.Any<Pipeline>(), null, Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 2L));

        var handler = new CreatePipelineCommandHandler(_appRepo, _pipelineRepo, _auditRepo, _queryContext);

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("UniquePipeline");
        result.IsActive.Should().BeFalse();
        await _pipelineRepo.Received(1).CreateAsync(Arg.Is<Pipeline>(p => p.Name == "UniquePipeline" && p.IsActive == false), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_DuplicateName_ThrowsDuplicateException()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var existingPipeline = new Pipeline
        {
            Id = 2L,
            PublicId = pipelinePublicId,
            AppId = 1L,
            Name = "OriginalPipeline"
        };
        var command = new UpdatePipelineCommand(pipelinePublicId, "DuplicatePipelineName", "New desc", true, new byte[] { 1 });

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(existingPipeline);
        _pipelineRepo.NameExistsForUserAsync(100L, "DuplicatePipelineName", Arg.Any<CancellationToken>()).Returns(true);

        var handler = new UpdatePipelineCommandHandler(_pipelineRepo, _auditRepo, _queryContext, _appRepo, _tableRepo, _fieldRepo, _appAccessService, Substitute.For<ITenantRepository>(), Substitute.For<IMainPipelineQueueRepository>(), Substitute.For<IServiceProvider>());

        // Act & Assert
        await handler.Invoking(h => h.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task UpdatePipeline_NameUnchanged_Succeeds()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var existingPipeline = new Pipeline
        {
            Id = 2L,
            PublicId = pipelinePublicId,
            AppId = 1L,
            Name = "OriginalPipeline"
        };
        var command = new UpdatePipelineCommand(pipelinePublicId, "OriginalPipeline", "New desc", true, new byte[] { 1 });

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(existingPipeline);
        _pipelineRepo.UpdateAsync(Arg.Any<Pipeline>(), null, Arg.Any<CancellationToken>()).Returns(1);

        var handler = new UpdatePipelineCommandHandler(_pipelineRepo, _auditRepo, _queryContext, _appRepo, _tableRepo, _fieldRepo, _appAccessService, Substitute.For<ITenantRepository>(), Substitute.For<IMainPipelineQueueRepository>(), Substitute.For<IServiceProvider>());

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).UpdateAsync(Arg.Is<Pipeline>(p => p.Name == "OriginalPipeline" && p.Description == "New desc"), null, Arg.Any<CancellationToken>());
        await _pipelineRepo.DidNotReceive().NameExistsForUserAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_UniqueNewName_Succeeds()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var existingPipeline = new Pipeline
        {
            Id = 2L,
            PublicId = pipelinePublicId,
            AppId = 1L,
            Name = "OriginalPipeline"
        };
        var command = new UpdatePipelineCommand(pipelinePublicId, "BrandNewPipeline", "New desc", true, new byte[] { 1 });

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(existingPipeline);
        _pipelineRepo.NameExistsForUserAsync(100L, "BrandNewPipeline", Arg.Any<CancellationToken>()).Returns(false);
        _pipelineRepo.UpdateAsync(Arg.Any<Pipeline>(), null, Arg.Any<CancellationToken>()).Returns(1);

        var handler = new UpdatePipelineCommandHandler(_pipelineRepo, _auditRepo, _queryContext, _appRepo, _tableRepo, _fieldRepo, _appAccessService, Substitute.For<ITenantRepository>(), Substitute.For<IMainPipelineQueueRepository>(), Substitute.For<IServiceProvider>());

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).UpdateAsync(Arg.Is<Pipeline>(p => p.Name == "BrandNewPipeline" && p.Description == "New desc"), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_ActivateWithIncompleteStep_ThrowsValidationException()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var existingPipeline = new Pipeline
        {
            Id = 2L,
            PublicId = pipelinePublicId,
            AppId = 1L,
            Name = "OriginalPipeline"
        };
        var command = new UpdatePipelineCommand(pipelinePublicId, "OriginalPipeline", "New desc", true, new byte[] { 1 });

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(existingPipeline);
        
        var incompleteSteps = new List<PipelineStep>
        {
            new() { PublicId = Guid.NewGuid(), RefId = "ref_search", Type = "query", Subtype = "search-records", IsValidated = false }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(existingPipeline.Id, Arg.Any<CancellationToken>()).Returns(incompleteSteps);

        var handler = new UpdatePipelineCommandHandler(_pipelineRepo, _auditRepo, _queryContext, _appRepo, _tableRepo, _fieldRepo, _appAccessService, Substitute.For<ITenantRepository>(), Substitute.For<IMainPipelineQueueRepository>(), Substitute.For<IServiceProvider>());

        // Act & Assert
        var exception = await handler.Invoking(h => h.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
        exception.Which.Errors.Should().ContainKey("Steps");
        exception.Which.Errors["Steps"].Should().Contain("Cannot activate PowerFlow with incomplete step configurations.");
    }

    [Fact]
    public async Task UpdatePipeline_TransitionToActive_ResumesSentinelPausedJobs()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var existingPipeline = new Pipeline
        {
            Id = 2L,
            PublicId = pipelinePublicId,
            AppId = 1L,
            Name = "OriginalPipeline",
            IsActive = false
        };
        var command = new UpdatePipelineCommand(pipelinePublicId, "OriginalPipeline", "New desc", true, new byte[] { 1 });

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(existingPipeline);
        _pipelineRepo.UpdateAsync(Arg.Any<Pipeline>(), null, Arg.Any<CancellationToken>()).Returns(1);

        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();

        var handler = new UpdatePipelineCommandHandler(_pipelineRepo, _auditRepo, _queryContext, _appRepo, _tableRepo, _fieldRepo, _appAccessService, Substitute.For<ITenantRepository>(), queueRepo, Substitute.For<IServiceProvider>());

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await queueRepo.Received(1).ResumePendingJobsAsync(Arg.Any<long>(), existingPipeline.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_ActiveToActive_DoesNotCallResumeOrPause()
    {
        // Arrange
        var pipelinePublicId = Guid.NewGuid();
        var existingPipeline = new Pipeline
        {
            Id = 2L,
            PublicId = pipelinePublicId,
            AppId = 1L,
            Name = "OriginalPipeline",
            IsActive = true
        };
        var command = new UpdatePipelineCommand(pipelinePublicId, "OriginalPipeline", "New desc", true, new byte[] { 1 });

        _pipelineRepo.GetByPublicIdAsync(pipelinePublicId, Arg.Any<CancellationToken>()).Returns(existingPipeline);
        _pipelineRepo.UpdateAsync(Arg.Any<Pipeline>(), null, Arg.Any<CancellationToken>()).Returns(1);

        var queueRepo = Substitute.For<IMainPipelineQueueRepository>();

        var handler = new UpdatePipelineCommandHandler(_pipelineRepo, _auditRepo, _queryContext, _appRepo, _tableRepo, _fieldRepo, _appAccessService, Substitute.For<ITenantRepository>(), queueRepo, Substitute.For<IServiceProvider>());

        // Act
        await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        await queueRepo.DidNotReceive().ResumePendingJobsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await queueRepo.DidNotReceive().PausePendingJobsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
