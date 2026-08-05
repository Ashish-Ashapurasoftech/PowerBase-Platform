using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Auth.Commands.UpdateMyPreferences;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.UnitTests.Auth;

public class UpdateMyPreferencesCommandHandlerTests
{
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    public UpdateMyPreferencesCommandHandlerTests()
    {
        _queryContext.UserId.Returns(5L);
        _userRepo.GetByIdAsync(5L).Returns(new User { Id = 5, PublicId = Guid.NewGuid(), Name = "Alice" });
    }

    private UpdateMyPreferencesCommandHandler CreateSut() => new(_userRepo, _queryContext);

    [Fact]
    public async Task HandleAsync_ValidPreferences_PersistsSerializedJson()
    {
        var sut = CreateSut();
        var prefs = new UserPreferencesSettings { Theme = "abyss", Accent = "emerald", PageSize = 25 };

        await sut.HandleAsync(new UpdateMyPreferencesCommand(prefs));

        await _userRepo.Received(1).UpdatePreferencesAsync(
            5L,
            Arg.Is<string>(json => json.Contains("abyss") && json.Contains("emerald") && json.Contains("25")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnknownTheme_ThrowsValidationException()
    {
        var sut = CreateSut();
        var prefs = new UserPreferencesSettings { Theme = "not-a-real-theme" };

        await sut.Invoking(s => s.HandleAsync(new UpdateMyPreferencesCommand(prefs)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_InvalidPageSize_ThrowsValidationException()
    {
        var sut = CreateSut();
        var prefs = new UserPreferencesSettings { PageSize = 7 };

        await sut.Invoking(s => s.HandleAsync(new UpdateMyPreferencesCommand(prefs)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_UnauthenticatedUser_ThrowsUnauthorizedActionException()
    {
        _queryContext.UserId.Returns(0L);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new UpdateMyPreferencesCommand(new UserPreferencesSettings())))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }
}
