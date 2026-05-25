using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.DeleteApp;
using PowerBase.Application.Apps.Queries.GetApp;
using PowerBase.Application.Apps.Queries.ListApps;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Apps;

public class AppQueryHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    public AppQueryHandlerTests()
    {
        _queryContext.UserId.Returns(1L);
    }

    [Fact]
    public async Task GetApp_ReturnsAppFromRepo()
    {
        var id = Guid.NewGuid();
        var app = new App { PublicId = id, Name = "Test App" };
        _appRepo.GetByPublicIdAsync(id).Returns(app);
        var sut = new GetAppQueryHandler(_appRepo);

        var result = await sut.HandleAsync(new GetAppQuery(id));

        result.Should().BeSameAs(app);
    }

    [Fact]
    public async Task ListApps_ReturnsPagedResult()
    {
        var apps = new List<AppListItemDto> { new() { Name = "A" }, new() { Name = "B" } };
        _appRepo.ListByUserAsync(1L, 1, 20).Returns(apps);
        _appRepo.CountByUserAsync(1L).Returns(2);
        var sut = new ListAppsQueryHandler(_appRepo, _queryContext);

        var result = await sut.HandleAsync(new ListAppsQuery(1, 20));

        result.Items.Should().HaveCount(2);
        result.Total.Should().Be(2);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task ListApps_PageBelowOne_NormalizesToOne(int inputPage, int expectedPage)
    {
        _appRepo.ListByUserAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>()).Returns(new List<AppListItemDto>());
        _appRepo.CountByUserAsync(Arg.Any<long>()).Returns(0);
        var sut = new ListAppsQueryHandler(_appRepo, _queryContext);

        var result = await sut.HandleAsync(new ListAppsQuery(inputPage, 20));

        result.Page.Should().Be(expectedPage);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(101, 20)]
    [InlineData(-1, 20)]
    public async Task ListApps_InvalidPageSize_NormalizesToTwenty(int inputPageSize, int expectedPageSize)
    {
        _appRepo.ListByUserAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>()).Returns(new List<AppListItemDto>());
        _appRepo.CountByUserAsync(Arg.Any<long>()).Returns(0);
        var sut = new ListAppsQueryHandler(_appRepo, _queryContext);

        var result = await sut.HandleAsync(new ListAppsQuery(1, inputPageSize));

        result.PageSize.Should().Be(expectedPageSize);
    }

    [Fact]
    public async Task DeleteApp_CallsDeleteOnRepo()
    {
        var id = Guid.NewGuid();
        var sut = new DeleteAppCommandHandler(_appRepo, _auditRepo);

        await sut.HandleAsync(new DeleteAppCommand(id));

        await _appRepo.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }
}
