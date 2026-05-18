using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.DeleteApp;
using PowerBase.Application.Apps.Queries.GetApp;
using PowerBase.Application.Apps.Queries.ListApps;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Apps;

public class AppQueryHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();

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
        var apps = new List<App> { new() { Name = "A" }, new() { Name = "B" } };
        _appRepo.ListAsync(1, 20).Returns(apps);
        _appRepo.CountAsync().Returns(2);
        var sut = new ListAppsQueryHandler(_appRepo);

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
        _appRepo.ListAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<App>());
        _appRepo.CountAsync().Returns(0);
        var sut = new ListAppsQueryHandler(_appRepo);

        var result = await sut.HandleAsync(new ListAppsQuery(inputPage, 20));

        result.Page.Should().Be(expectedPage);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(101, 20)]
    [InlineData(-1, 20)]
    public async Task ListApps_InvalidPageSize_NormalizesToTwenty(int inputPageSize, int expectedPageSize)
    {
        _appRepo.ListAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(new List<App>());
        _appRepo.CountAsync().Returns(0);
        var sut = new ListAppsQueryHandler(_appRepo);

        var result = await sut.HandleAsync(new ListAppsQuery(1, inputPageSize));

        result.PageSize.Should().Be(expectedPageSize);
    }

    [Fact]
    public async Task DeleteApp_CallsDeleteOnRepo()
    {
        var id = Guid.NewGuid();
        var sut = new DeleteAppCommandHandler(_appRepo);

        await sut.HandleAsync(new DeleteAppCommand(id));

        await _appRepo.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }
}
