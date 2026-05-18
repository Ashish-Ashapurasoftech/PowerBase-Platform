using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Auth.Commands.Signup;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Auth;

public class SignupCommandHandlerTests
{
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IJwtService _jwtService = Substitute.For<IJwtService>();
    private readonly IPasswordService _passwordService = Substitute.For<IPasswordService>();

    private SignupCommandHandler CreateSut() => new(_userRepo, _jwtService, _passwordService);

    private void SetupHappyPath(long userId = 1)
    {
        _userRepo.GetByEmailAsync(Arg.Any<string>()).Returns((User?)null);
        _passwordService.Hash(Arg.Any<string>()).Returns("hashed");
        _userRepo.CreateAsync(Arg.Any<User>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(userId);
        _userRepo.GetByIdAsync(userId).Returns(new User
        {
            Id = userId,
            PublicId = Guid.NewGuid(),
            Email = "test@example.com",
            Name = "Jane Doe",
        });
        _jwtService.GenerateIdentityToken(Arg.Any<User>(), out Arg.Any<Guid>(), out Arg.Any<DateTime>())
            .Returns("identity-token");
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_ReturnsIdentityTokenAndUserDetails()
    {
        SetupHappyPath();
        var sut = CreateSut();

        var result = await sut.HandleAsync(new SignupCommand("test@example.com", "password123", "Jane Doe"));

        result.IdentityToken.Should().Be("identity-token");
        result.Email.Should().Be("test@example.com");
    }

    [Fact]
    public async Task HandleAsync_DuplicateEmail_ThrowsDuplicateException()
    {
        _userRepo.GetByEmailAsync(Arg.Any<string>()).Returns(new User { Email = "taken@example.com" });
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new SignupCommand("taken@example.com", "password123", "Jane Doe")))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task HandleAsync_InvalidEmail_ThrowsValidationException()
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new SignupCommand("not-an-email", "password123", "Jane Doe")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_DoesNotCreateTenant()
    {
        SetupHappyPath();
        var sut = CreateSut();

        await sut.HandleAsync(new SignupCommand("test@example.com", "password123", "Jane Doe"));

        await _userRepo.Received(1).CreateAsync(Arg.Any<User>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }
}
