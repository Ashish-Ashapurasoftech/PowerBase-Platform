using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Queries.ListPipelineRuns;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

public class ListAppPipelineRunsQueryHandlerTests
{
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly Guid appPublicId = Guid.NewGuid();
    private readonly ListAppPipelineRunsQueryHandler handler;

    public ListAppPipelineRunsQueryHandlerTests()
    {
        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(7L);
        handler = new ListAppPipelineRunsQueryHandler(_pipelineRepo, _appRepo, _userRepo);
    }

    [Fact]
    public async Task Handle_ResolvesAppAndListsRunsAcrossItsPipelines()
    {
        var run = new PipelineRun { PublicId = Guid.NewGuid(), Status = "Success", TriggerType = "webhook", StartedOn = DateTime.UtcNow, TriggeredBy = 0, AttemptCount = 1 };
        _pipelineRepo.CountRunsByAppIdAsync(7L, null, null, null, Arg.Any<CancellationToken>()).Returns(1);
        _pipelineRepo.GetRunsByAppIdAsync(7L, null, null, null, 1, 10, Arg.Any<CancellationToken>())
            .Returns(new List<(PipelineRun, string, Guid)> { (run, "My Flow", Guid.NewGuid()) });

        var result = await handler.HandleAsync(new ListAppPipelineRunsQuery(appPublicId));

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("My Flow", result.Items[0].PipelineName);
        Assert.Equal("System", result.Items[0].TriggeredByUser);
    }

    [Fact]
    public async Task Handle_UnknownApp_ThrowsNotFound()
    {
        _appRepo.GetIdByPublicIdAsync(Guid.NewGuid(), Arg.Any<CancellationToken>()).Returns(0L);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new ListAppPipelineRunsQuery(Guid.NewGuid())));
    }

    [Fact]
    public async Task Handle_PipelineFilterFromAnotherApp_ReturnsEmptyInsteadOfLeakingCrossAppData()
    {
        var otherAppsPipeline = new Pipeline { Id = 55, AppId = 999, PublicId = Guid.NewGuid() };
        _pipelineRepo.GetByPublicIdAsync(otherAppsPipeline.PublicId, Arg.Any<CancellationToken>()).Returns(otherAppsPipeline);

        var result = await handler.HandleAsync(new ListAppPipelineRunsQuery(appPublicId, otherAppsPipeline.PublicId));

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
        await _pipelineRepo.DidNotReceive().GetRunsByAppIdAsync(Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ToDate_IsTreatedAsInclusiveOfTheWholeDay()
    {
        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 1, 5);
        _pipelineRepo.CountRunsByAppIdAsync(7L, null, from, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()).Returns(0);
        _pipelineRepo.GetRunsByAppIdAsync(7L, null, from, Arg.Any<DateTime?>(), 1, 10, Arg.Any<CancellationToken>())
            .Returns(new List<(PipelineRun, string, Guid)>());

        await handler.HandleAsync(new ListAppPipelineRunsQuery(appPublicId, null, from, to));

        await _pipelineRepo.Received(1).GetRunsByAppIdAsync(7L, null, from,
            Arg.Is<DateTime?>(d => d != null && d.Value.Date == to.Date && d.Value > to), 1, 10, Arg.Any<CancellationToken>());
    }
}
