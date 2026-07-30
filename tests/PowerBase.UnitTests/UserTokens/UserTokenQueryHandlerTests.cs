using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.UserTokens.Common;
using PowerBase.Application.UserTokens.Queries.GetAdminUserTokens;
using PowerBase.Application.UserTokens.Queries.GetMyUserTokens;
using PowerBase.Application.UserTokens.Queries.GetSingleTokenDetail;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.UserTokens;

public class UserTokenQueryHandlerTests
{
    private readonly IUserTokenRepository _userTokenRepository = Substitute.For<IUserTokenRepository>();
    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    public UserTokenQueryHandlerTests()
    {
        _queryContext.UserId.Returns(1001);
        _queryContext.TenantId.Returns(500);
    }

    [Fact]
    public async Task GetAdminUserTokens_ReturnsPagedAndMaskedTokens()
    {
        // Arrange
        var publicId = Guid.NewGuid();
        var adminTokens = new List<AdminUserTokenDto>
        {
            new AdminUserTokenDto
            {
                PublicId = publicId,
                TokenName = "Admin Token",
                TokenPrefix = "ctg8************yhbn",
                AccessAllApps = true,
                IsActive = true,
                UserId = 1001,
                OwnerName = "Test User",
                OwnerEmail = "test@example.com"
            }
        };

        _userTokenRepository.GetAdminTokensPagedAsync(500, null, null, 1, 20, Arg.Any<CancellationToken>())
            .Returns(((IEnumerable<AdminUserTokenDto>)adminTokens, 1));

        var handler = new GetAdminUserTokensQueryHandler(_userTokenRepository, _userRepository, _queryContext);
        var query = new GetAdminUserTokensQuery { Page = 1, PageSize = 20 };

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("ctg8************yhbn", result.Items.First().TokenPrefix);
    }

    [Fact]
    public async Task GetSingleTokenDetail_ExistingToken_ReturnsDetail()
    {
        // Arrange
        var publicId = Guid.NewGuid();
        var token = new UserToken
        {
            Id = 10,
            PublicId = publicId,
            TenantId = 500,
            UserId = 1001,
            TokenName = "Single Token",
            TokenPrefix = "pb_ut_1234...",
            AccessAllApps = true,
            IsActive = true
        };

        _userTokenRepository.GetByPublicIdAsync(publicId, 500, Arg.Any<CancellationToken>())
            .Returns(token);

        var handler = new GetSingleTokenDetailQueryHandler(_userTokenRepository, _userRepository, _queryContext);
        var query = new GetSingleTokenDetailQuery(publicId);

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Single Token", result!.TokenName);
        Assert.Equal(publicId, result.PublicId);
    }

    [Fact]
    public async Task GetMyTokens_ReturnsTokensList()
    {
        // Arrange
        var tokens = new List<UserToken>
        {
            new UserToken
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                TenantId = 500,
                UserId = 1001,
                TokenName = "My Token",
                TokenPrefix = "pb_ut_mine...",
                AccessAllApps = true,
                IsActive = true
            }
        };

        _userTokenRepository.GetMyTokensAsync(1001, 500, Arg.Any<CancellationToken>())
            .Returns(tokens);

        var handler = new GetMyUserTokensQueryHandler(_userTokenRepository, _queryContext);
        var query = new GetMyUserTokensQuery();

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("My Token", result.First().TokenName);
    }
}
