using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Auth.Commands.UpdateUserProfile;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Auth;

public class UpdateUserProfileCommandHandlerTests
{
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private UpdateUserProfileCommandHandler CreateSut() => new(_userRepo, _queryContext);

    [Fact]
    public async Task HandleAsync_AuthenticatedUser_UpdatesProfileAndReturnsUpdatedUser()
    {
        // Arrange
        const long userId = 10L;
        _queryContext.UserId.Returns(userId);

        var updatedUser = new User
        {
            Id = userId,
            PublicId = Guid.NewGuid(),
            Email = "john.doe@example.com",
            Name = "John Doe",
            FirstName = "John",
            LastName = "Doe"
        };

        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(updatedUser);

        _userRepo.UpdateProfileAsync(userId, "John", "Doe", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var command = new UpdateUserProfileCommand("  John  ", "  Doe  ");

        // Act
        var result = await sut.HandleAsync(command);

        // Assert
        result.Should().NotBeNull();
        result.FirstName.Should().Be("John");
        result.LastName.Should().Be("Doe");
        result.Name.Should().Be("John Doe");
        await _userRepo.Received(1).UpdateProfileAsync(userId, "John", "Doe", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnauthenticatedUser_ThrowsUnauthorizedActionException()
    {
        // Arrange
        _queryContext.UserId.Returns(0L);
        var sut = CreateSut();

        var command = new UpdateUserProfileCommand("John", "Doe");

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task HandleAsync_UserNotFound_ThrowsNotFoundException()
    {
        // Arrange
        const long userId = 99L;
        _queryContext.UserId.Returns(userId);

        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotFoundException("User", userId));

        var sut = CreateSut();
        var command = new UpdateUserProfileCommand("Jane", "Smith");

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<NotFoundException>();
    }
}
