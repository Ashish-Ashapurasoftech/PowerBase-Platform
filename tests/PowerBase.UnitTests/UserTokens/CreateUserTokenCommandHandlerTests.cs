using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Commands.CreateUserToken;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.UserTokens;

public class CreateUserTokenCommandHandlerTests
{
    private readonly IUserTokenRepository _userTokenRepository = Substitute.For<IUserTokenRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepository = Substitute.For<IAuditRepository>();
    private readonly CreateUserTokenCommandHandler _handler;

    public CreateUserTokenCommandHandlerTests()
    {
        _queryContext.UserId.Returns(1001);
        _queryContext.TenantId.Returns(500);

        _handler = new CreateUserTokenCommandHandler(_userTokenRepository, _queryContext, _auditRepository);
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_CreatesUserTokenAndReturnsSecretOnce()
    {
        // Arrange
        var command = new CreateUserTokenCommand
        {
            TokenName = "Test Integration Token",
            Description = "Description test",
            AccessAllApps = true,
            AllowedAppPublicIds = null
        };

        _userTokenRepository.CreateAsync(Arg.Any<UserToken>(), Arg.Any<IEnumerable<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var token = callInfo.Arg<UserToken>();
                token.Id = 10;
                return token;
            });

        // Act
        var result = await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Integration Token", result.TokenName);
        Assert.True(result.AccessAllApps);
        Assert.StartsWith("pb_ut_", result.PlainTextToken);
        Assert.Empty(result.AllowedAppPublicIds);

        await _userTokenRepository.Received(1).CreateAsync(
            Arg.Is<UserToken>(t => t.UserId == 1001 && t.TenantId == 500 && t.TokenName == "Test Integration Token"),
            Arg.Any<IEnumerable<Guid>?>(),
            Arg.Any<CancellationToken>()
        );
    }
}
