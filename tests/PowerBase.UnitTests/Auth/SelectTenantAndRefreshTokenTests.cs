using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using PowerBase.Application.Auth.Commands.RefreshToken;
using PowerBase.Application.Auth.Commands.SelectTenant;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Auth;

public class SelectTenantAndRefreshTokenTests
{
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly IAuditRepository _auditRepository = Substitute.For<IAuditRepository>();
    private readonly IJwtService _jwtService = Substitute.For<IJwtService>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private readonly SelectTenantCommandHandler _selectTenantHandler;
    private readonly RefreshTokenCommandHandler _refreshTokenHandler;

    public SelectTenantAndRefreshTokenTests()
    {
        _queryContext.UserId.Returns(1001);
        _queryContext.TenantId.Returns(500);
        _queryContext.IpAddress.Returns("127.0.0.1");

        _selectTenantHandler = new SelectTenantCommandHandler(
            _tenantRepository,
            _userRepository,
            _auditRepository,
            _jwtService,
            _queryContext
        );

        _refreshTokenHandler = new RefreshTokenCommandHandler(
            _tenantRepository,
            _userRepository,
            _auditRepository,
            _jwtService,
            _queryContext
        );
    }

    [Fact]
    public async Task SelectTenant_ValidRequest_GeneratesTokenAndCreatesSession()
    {
        // Arrange
        var tenantPublicId = Guid.NewGuid();
        var tenant = new Tenant { Id = 500, PublicId = tenantPublicId, Name = "Acme Corp" };
        var user = new User { Id = 1001, PublicId = Guid.NewGuid(), Email = "user@acme.com", Name = "User Name" };
        var expectedJwtId = Guid.NewGuid();
        var expectedExpiresAt = DateTime.UtcNow.AddHours(1);

        _tenantRepository.GetTenantForUserAsync(tenantPublicId, 1001, Arg.Any<CancellationToken>())
            .Returns(tenant);

        _userRepository.GetByIdAsync(1001, Arg.Any<CancellationToken>())
            .Returns(user);

        _tenantRepository.GetUserRoleNameInTenantAsync(1001, 500, Arg.Any<CancellationToken>())
            .Returns("Administrator");

        Guid dummyGuid;
        DateTime dummyDateTime;
        _jwtService.GenerateToken(
            user,
            500,
            "Administrator",
            out dummyGuid,
            out dummyDateTime
        ).Returns(callInfo =>
        {
            callInfo[3] = expectedJwtId;
            callInfo[4] = expectedExpiresAt;
            return "mocked-jwt-token";
        });

        var command = new SelectTenantCommand(tenantPublicId);

        // Act
        var result = await _selectTenantHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("mocked-jwt-token", result.Token);
        Assert.Equal(expectedExpiresAt, result.ExpiresAt);
        Assert.Equal(user.PublicId, result.UserPublicId);
        Assert.Equal(user.Email, result.Email);
        Assert.Equal(user.Name, result.Name);
        Assert.Equal(tenantPublicId, result.TenantPublicId);
        Assert.Equal("Acme Corp", result.TenantName);

        await _auditRepository.Received(1).CreateSessionAsync(
            1001,
            500,
            expectedJwtId,
            "127.0.0.1",
            expectedExpiresAt,
            null,
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task SelectTenant_EmptyTenantId_ThrowsValidationException()
    {
        // Arrange
        var command = new SelectTenantCommand(Guid.Empty);

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() => _selectTenantHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task SelectTenant_UserRoleNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var tenantPublicId = Guid.NewGuid();
        var tenant = new Tenant { Id = 500, PublicId = tenantPublicId, Name = "Acme Corp" };
        var user = new User { Id = 1001, PublicId = Guid.NewGuid(), Email = "user@acme.com", Name = "User Name" };

        _tenantRepository.GetTenantForUserAsync(tenantPublicId, 1001, Arg.Any<CancellationToken>())
            .Returns(tenant);

        _userRepository.GetByIdAsync(1001, Arg.Any<CancellationToken>())
            .Returns(user);

        _tenantRepository.GetUserRoleNameInTenantAsync(1001, 500, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var command = new SelectTenantCommand(tenantPublicId);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() => _selectTenantHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task RefreshToken_ValidRequest_GeneratesNewTokenAndCreatesSession()
    {
        // Arrange
        var tenantPublicId = Guid.NewGuid();
        var tenant = new Tenant { Id = 500, PublicId = tenantPublicId, Name = "Acme Corp" };
        var user = new User { Id = 1001, PublicId = Guid.NewGuid(), Email = "user@acme.com", Name = "User Name" };
        var expectedJwtId = Guid.NewGuid();
        var expectedExpiresAt = DateTime.UtcNow.AddHours(1);

        _tenantRepository.GetByIdAsync(500, Arg.Any<CancellationToken>())
            .Returns(tenant);

        _userRepository.GetByIdAsync(1001, Arg.Any<CancellationToken>())
            .Returns(user);

        _tenantRepository.GetUserRoleNameInTenantAsync(1001, 500, Arg.Any<CancellationToken>())
            .Returns("Member");

        Guid dummyGuid;
        DateTime dummyDateTime;
        _jwtService.GenerateToken(
            user,
            500,
            "Member",
            out dummyGuid,
            out dummyDateTime
        ).Returns(callInfo =>
        {
            callInfo[3] = expectedJwtId;
            callInfo[4] = expectedExpiresAt;
            return "refreshed-jwt-token";
        });

        var command = new RefreshTokenCommand();

        // Act
        var result = await _refreshTokenHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("refreshed-jwt-token", result.Token);
        Assert.Equal(expectedExpiresAt, result.ExpiresAt);
        Assert.Equal(user.PublicId, result.UserPublicId);
        Assert.Equal(user.Email, result.Email);
        Assert.Equal(user.Name, result.Name);
        Assert.Equal(tenantPublicId, result.TenantPublicId);
        Assert.Equal("Acme Corp", result.TenantName);

        await _auditRepository.Received(1).CreateSessionAsync(
            1001,
            500,
            expectedJwtId,
            "127.0.0.1",
            expectedExpiresAt,
            null,
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task RefreshToken_UserRoleNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var tenantPublicId = Guid.NewGuid();
        var tenant = new Tenant { Id = 500, PublicId = tenantPublicId, Name = "Acme Corp" };
        var user = new User { Id = 1001, PublicId = Guid.NewGuid() };

        _tenantRepository.GetByIdAsync(500, Arg.Any<CancellationToken>())
            .Returns(tenant);

        _userRepository.GetByIdAsync(1001, Arg.Any<CancellationToken>())
            .Returns(user);

        _tenantRepository.GetUserRoleNameInTenantAsync(1001, 500, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var command = new RefreshTokenCommand();

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() => _refreshTokenHandler.HandleAsync(command, CancellationToken.None));
    }
}
