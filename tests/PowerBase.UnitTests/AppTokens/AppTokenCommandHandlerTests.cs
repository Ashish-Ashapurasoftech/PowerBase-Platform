using NSubstitute;
using PowerBase.Application.AppTokens.Commands.CreateAppToken;
using PowerBase.Application.AppTokens.Commands.DeleteAppToken;
using PowerBase.Application.AppTokens.Commands.RotateAppToken;
using PowerBase.Application.AppTokens.Commands.UpdateAppTokenStatus;
using PowerBase.Application.AppTokens.Queries.GetAppTokens;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.AppTokens;

public class AppTokenCommandHandlerTests
{
    private readonly IAppTokenRepository _appTokenRepository = Substitute.For<IAppTokenRepository>();
    private readonly IAppRepository _appRepository = Substitute.For<IAppRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private readonly CreateAppTokenCommandHandler _createHandler;
    private readonly UpdateAppTokenStatusCommandHandler _updateStatusHandler;
    private readonly RotateAppTokenCommandHandler _rotateHandler;
    private readonly DeleteAppTokenCommandHandler _deleteHandler;
    private readonly GetAppTokensQueryHandler _getAppTokensHandler;

    public AppTokenCommandHandlerTests()
    {
        _queryContext.UserId.Returns(1001);
        _queryContext.TenantId.Returns(500);

        _createHandler = new CreateAppTokenCommandHandler(_appTokenRepository, _appRepository, _queryContext);
        _updateStatusHandler = new UpdateAppTokenStatusCommandHandler(_appTokenRepository, _queryContext);
        _rotateHandler = new RotateAppTokenCommandHandler(_appTokenRepository, _queryContext);
        _deleteHandler = new DeleteAppTokenCommandHandler(_appTokenRepository, _queryContext);
        _getAppTokensHandler = new GetAppTokensQueryHandler(_appTokenRepository, _queryContext);
    }

    [Fact]
    public async Task CreateAppToken_ValidApp_ReturnsCreatedToken()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        _appRepository.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>())
            .Returns(45);

        _appTokenRepository.CreateAsync(Arg.Any<AppToken>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var token = callInfo.Arg<AppToken>();
                token.Id = 10;
                return token;
            });

        var command = new CreateAppTokenCommand
        {
            AppPublicId = appPublicId,
            TokenName = "Production Key",
            Description = "Token for integration"
        };

        // Act
        var result = await _createHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Production Key", result.TokenName);
        Assert.Equal(appPublicId, result.AppPublicId);
        Assert.Equal(1001, result.CreatedByUserId);
        Assert.StartsWith("pb_at_", result.PlainTextToken);
    }

    [Fact]
    public async Task CreateAppToken_AppNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        _appRepository.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>())
            .Returns(0);

        var command = new CreateAppTokenCommand
        {
            AppPublicId = appPublicId,
            TokenName = "Production Key"
        };

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() => _createHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateStatus_ExistingToken_UpdatesSuccessfully()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var publicId = Guid.NewGuid();

        _appTokenRepository.UpdateStatusAsync(publicId, 500, appPublicId, false, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new UpdateAppTokenStatusCommand
        {
            AppPublicId = appPublicId,
            PublicId = publicId,
            IsActive = false
        };

        // Act
        await _updateStatusHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _appTokenRepository.Received(1).UpdateStatusAsync(publicId, 500, appPublicId, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RotateToken_ValidToken_ReturnsNewSecret()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var publicId = Guid.NewGuid();
        var existingToken = new AppToken
        {
            Id = 99,
            PublicId = publicId,
            TenantId = 500,
            AppId = 45,
            CreatedByUserId = 1001,
            TokenName = "Webhook Key",
            TokenPrefix = "pb_at_1234...",
            IsActive = true
        };

        _appTokenRepository.GetByPublicIdAsync(publicId, 500, appPublicId, Arg.Any<CancellationToken>())
            .Returns(existingToken);

        _appTokenRepository.RotateSecretAsync(99, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await _rotateHandler.HandleAsync(appPublicId, publicId, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Webhook Key", result.TokenName);
        Assert.StartsWith("pb_at_", result.PlainTextToken);
    }

    [Fact]
    public async Task DeleteToken_ExistingToken_DeletesSuccessfully()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var publicId = Guid.NewGuid();

        _appTokenRepository.DeleteAsync(publicId, 500, appPublicId, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await _deleteHandler.HandleAsync(appPublicId, publicId, CancellationToken.None);

        // Assert
        await _appTokenRepository.Received(1).DeleteAsync(publicId, 500, appPublicId, Arg.Any<CancellationToken>());
    }
}
