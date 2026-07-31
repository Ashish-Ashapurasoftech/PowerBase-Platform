using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Commands.RevokeUserToken;
using PowerBase.Application.UserTokens.Commands.RotateUserToken;
using PowerBase.Application.UserTokens.Commands.UpdateUserToken;
using PowerBase.Application.UserTokens.Commands.UpdateUserTokenStatus;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.UserTokens;

public class UserTokenCommandHandlerTests
{
    private readonly IUserTokenRepository _userTokenRepository = Substitute.For<IUserTokenRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly RotateUserTokenCommandHandler _rotateHandler;
    private readonly UpdateUserTokenStatusCommandHandler _updateStatusHandler;

    public UserTokenCommandHandlerTests()
    {
        _queryContext.UserId.Returns(1001);
        _queryContext.TenantId.Returns(500);

        _rotateHandler = new RotateUserTokenCommandHandler(_userTokenRepository, _queryContext);
        _updateStatusHandler = new UpdateUserTokenStatusCommandHandler(_userTokenRepository, _queryContext);
    }

    [Fact]
    public async Task RotateToken_ValidToken_ReturnsNewSecret()
    {
        // Arrange
        var publicId = Guid.NewGuid();
        var existingToken = new UserToken
        {
            Id = 123,
            PublicId = publicId,
            TenantId = 500,
            UserId = 1001,
            TokenName = "My Secret Token",
            TokenPrefix = "pb_ut_old1...",
            IsActive = true,
            AccessAllApps = true
        };

        _userTokenRepository.GetByPublicIdAsync(publicId, 500, Arg.Any<CancellationToken>())
            .Returns(existingToken);

        _userTokenRepository.RotateSecretAsync(123, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new RotateUserTokenCommand(publicId);

        // Act
        var result = await _rotateHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("My Secret Token", result.TokenName);
        Assert.StartsWith("pb_ut_", result.PlainTextToken);
        await _userTokenRepository.Received(1).RotateSecretAsync(123, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RotateToken_NonExistingToken_ThrowsNotFoundException()
    {
        // Arrange
        var publicId = Guid.NewGuid();
        _userTokenRepository.GetByPublicIdAsync(publicId, 500, Arg.Any<CancellationToken>())
            .Returns((UserToken?)null);

        var command = new RotateUserTokenCommand(publicId);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() => _rotateHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateTokenStatus_BulkUpdate_ReturnsStatus()
    {
        // Arrange
        var publicIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _userTokenRepository.GetExistingPublicIdsAsync(Arg.Any<IEnumerable<Guid>>(), 500, Arg.Any<CancellationToken>())
            .Returns(publicIds);
        _userTokenRepository.UpdateStatusAsync(Arg.Any<IEnumerable<Guid>>(), 500, false, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new UpdateUserTokenStatusCommand
        {
            PublicIds = publicIds,
            IsActive = false
        };

        // Act
        var result = await _updateStatusHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        await _userTokenRepository.Received(1).UpdateStatusAsync(Arg.Any<IEnumerable<Guid>>(), 500, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTokenStatus_MissingId_ThrowsNotFoundException()
    {
        // Arrange
        var validId = Guid.NewGuid();
        var invalidId = Guid.NewGuid();
        var publicIds = new List<Guid> { validId, invalidId };

        _userTokenRepository.GetExistingPublicIdsAsync(publicIds, 500, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { validId });

        var command = new UpdateUserTokenStatusCommand
        {
            PublicIds = publicIds,
            IsActive = false
        };

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() => _updateStatusHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task RevokeToken_ValidPublicId_ReturnsTrue()
    {
        // Arrange
        var publicId = Guid.NewGuid();
        var revokeHandler = new RevokeUserTokenCommandHandler(_userTokenRepository, _queryContext);
        _userTokenRepository.RevokeAsync(publicId, 500, Arg.Any<CancellationToken>()).Returns(true);

        var command = new RevokeUserTokenCommand(publicId);

        // Act
        var result = await revokeHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        await _userTokenRepository.Received(1).RevokeAsync(publicId, 500, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateToken_ValidToken_UpdatesSuccessfully()
    {
        // Arrange
        var publicId = Guid.NewGuid();
        var existingToken = new UserToken
        {
            Id = 123,
            PublicId = publicId,
            TenantId = 500,
            UserId = 1001,
            TokenName = "Old Token Name",
            Description = "Old Description",
            IsActive = true,
            AccessAllApps = true
        };

        var allowedAppPublicIds = new List<Guid> { Guid.NewGuid() };

        _userTokenRepository.GetByPublicIdAsync(publicId, 500, Arg.Any<CancellationToken>())
            .Returns(existingToken);

        _userTokenRepository.UpdateDetailsAsync(
            123,
            "New Token Name",
            "New Description",
            false,
            allowedAppPublicIds,
            Arg.Any<CancellationToken>()
        ).Returns(true);

        var handler = new UpdateUserTokenCommandHandler(_userTokenRepository, _queryContext);
        var command = new UpdateUserTokenCommand
        {
            PublicId = publicId,
            TokenName = "New Token Name",
            Description = "New Description",
            AccessAllApps = false,
            AllowedAppPublicIds = allowedAppPublicIds
        };

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        await _userTokenRepository.Received(1).UpdateDetailsAsync(
            123,
            "New Token Name",
            "New Description",
            false,
            allowedAppPublicIds,
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task UpdateToken_NonExistingToken_ThrowsNotFoundException()
    {
        // Arrange
        var publicId = Guid.NewGuid();
        _userTokenRepository.GetByPublicIdAsync(publicId, 500, Arg.Any<CancellationToken>())
            .Returns((UserToken?)null);

        var handler = new UpdateUserTokenCommandHandler(_userTokenRepository, _queryContext);
        var command = new UpdateUserTokenCommand
        {
            PublicId = publicId,
            TokenName = "New Token Name"
        };

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(command, CancellationToken.None));
    }
}

