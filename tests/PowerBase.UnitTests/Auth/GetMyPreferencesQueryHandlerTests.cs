using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Auth.Queries.GetMyPreferences;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Auth;

public class GetMyPreferencesQueryHandlerTests
{
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private GetMyPreferencesQueryHandler CreateSut() => new(_userRepo, _queryContext);

    [Fact]
    public async Task HandleAsync_AuthenticatedUser_ReturnsUser()
    {
        var user = new User { Id = 5, PublicId = Guid.NewGuid(), Email = "me@example.com", Name = "Alice", Preferences = "{\"theme\":\"abyss\"}" };
        _queryContext.UserId.Returns(5L);
        _userRepo.GetByIdAsync(5L).Returns(user);
        var sut = CreateSut();

        var result = await sut.HandleAsync(new GetMyPreferencesQuery());

        result.Should().BeSameAs(user);
    }

    [Fact]
    public async Task HandleAsync_UnauthenticatedUser_ThrowsUnauthorizedActionException()
    {
        _queryContext.UserId.Returns(0L);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new GetMyPreferencesQuery()))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }
}
