using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.UpdateAppBranding;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.UnitTests.Apps;

public class UpdateAppBrandingCommandHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    private UpdateAppBrandingCommandHandler CreateSut() => new(_appRepo, _appAccessService);

    [Fact]
    public async Task HandleAsync_ValidBrandingAndLayout_PersistsBothAsSerializedJson()
    {
        var appId = Guid.NewGuid();
        _appRepo.GetByPublicIdAsync(appId).Returns(new App { PublicId = appId, Name = "Test App" });
        _appRepo.UpdateBrandingAsync(appId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        var branding = new AppBrandingSettings { Theme = "abyss", Accent = "emerald" };
        var layout = new AppLayoutSettings { NavPosition = "top" };

        await sut.HandleAsync(new UpdateAppBrandingCommand(appId, branding, layout));

        await _appRepo.Received(1).UpdateBrandingAsync(
            appId,
            Arg.Is<string>(json => json.Contains("abyss")),
            Arg.Is<string>(json => json.Contains("top")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_OnlyBrandingProvided_KeepsExistingLayoutUnchanged()
    {
        var appId = Guid.NewGuid();
        var existingLayoutJson = "{\"navPosition\":\"left\",\"sidebarStyle\":\"mini\"}";
        _appRepo.GetByPublicIdAsync(appId).Returns(new App { PublicId = appId, Name = "Test App", LayoutSettings = existingLayoutJson });
        _appRepo.UpdateBrandingAsync(appId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1);
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateAppBrandingCommand(appId, new AppBrandingSettings { Theme = "nord" }, null));

        await _appRepo.Received(1).UpdateBrandingAsync(
            appId, Arg.Any<string>(), existingLayoutJson, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnknownTheme_ThrowsValidationException()
    {
        var appId = Guid.NewGuid();
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateAppBrandingCommand(
                appId, new AppBrandingSettings { Theme = "not-a-real-theme" }, null)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_AppNotFound_ThrowsNotFoundException()
    {
        var appId = Guid.NewGuid();
        _appRepo.GetByPublicIdAsync(appId).Returns(new App { PublicId = appId, Name = "Test App" });
        _appRepo.UpdateBrandingAsync(appId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateAppBrandingCommand(appId, new AppBrandingSettings(), null)))
            .Should().ThrowAsync<NotFoundException>();
    }
}
