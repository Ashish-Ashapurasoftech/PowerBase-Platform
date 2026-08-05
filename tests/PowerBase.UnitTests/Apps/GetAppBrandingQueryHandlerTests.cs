using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Queries.GetAppBranding;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Apps;

public class GetAppBrandingQueryHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();

    [Fact]
    public async Task HandleAsync_ReturnsAppFromRepo()
    {
        var id = Guid.NewGuid();
        var app = new App { PublicId = id, Name = "Test App", Branding = "{\"theme\":\"slate\"}" };
        _appRepo.GetByPublicIdAsync(id).Returns(app);
        var sut = new GetAppBrandingQueryHandler(_appRepo);

        var result = await sut.HandleAsync(new GetAppBrandingQuery(id));

        result.Should().BeSameAs(app);
    }
}
