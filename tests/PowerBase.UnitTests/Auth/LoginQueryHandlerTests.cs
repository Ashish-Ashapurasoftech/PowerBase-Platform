using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Auth.Queries.Login;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Auth;

public class LoginQueryHandlerTests
{
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IJwtService _jwtService = Substitute.For<IJwtService>();
    private readonly IPasswordService _passwordService = Substitute.For<IPasswordService>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private LoginQueryHandler CreateSut() => new(
        _userRepo, _tenantRepo, _auditRepo, _jwtService, _passwordService, _queryContext);

    private static User MakeUser() => new()
    {
        Id = 1,
        PublicId = Guid.NewGuid(),
        Email = "user@example.com",
        HashedPassword = "hash",
        Name = "Jane Doe",
    };

    [Fact]
    public async Task HandleAsync_CorrectCredentials_ReturnsToken()
    {
        var user = MakeUser();
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);
        _passwordService.Verify("password123", "hash").Returns(true);
        _tenantRepo.GetActiveTenantIdByUserIdAsync(user.Id).Returns(42L);
        _jwtService.GenerateToken(user, 42L, out Arg.Any<Guid>()).Returns("token");
        _queryContext.IpAddress.Returns("127.0.0.1");
        var sut = CreateSut();

        var result = await sut.HandleAsync(new LoginQuery("user@example.com", "password123", "127.0.0.1"));

        result.Token.Should().Be("token");
        result.Email.Should().Be("user@example.com");
    }

    [Fact]
    public async Task HandleAsync_UnknownEmail_ThrowsUnauthorizedActionException()
    {
        _userRepo.GetByEmailAsync(Arg.Any<string>()).Returns((User?)null);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new LoginQuery("ghost@example.com", "pass", "127.0.0.1")))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task HandleAsync_WrongPassword_ThrowsUnauthorizedActionException()
    {
        var user = MakeUser();
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);
        _passwordService.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new LoginQuery("user@example.com", "wrong", "127.0.0.1")))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task HandleAsync_WrongPassword_RecordsFailedLoginAttempt()
    {
        var user = MakeUser();
        _userRepo.GetByEmailAsync("user@example.com").Returns(user);
        _passwordService.Verify(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new LoginQuery("user@example.com", "wrong", "1.2.3.4")))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await _auditRepo.Received().RecordLoginAttemptAsync(
            "user@example.com", "1.2.3.4", wasSuccessful: false,
            userId: user.Id, failureReason: Arg.Any<string?>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_InvalidEmail_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new LoginQuery("not-email", "pass", "127.0.0.1")))
            .Should().ThrowAsync<Domain.Exceptions.ValidationException>();
    }
}
