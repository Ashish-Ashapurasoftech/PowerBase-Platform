using System.Data;
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
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IJwtService _jwtService = Substitute.For<IJwtService>();
    private readonly IPasswordService _passwordService = Substitute.For<IPasswordService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private SignupCommandHandler CreateSut() => new(
        _userRepo, _tenantRepo, _auditRepo, _jwtService, _passwordService, _uow, _queryContext);

    private void SetupHappyPath(long userId = 1, long tenantId = 2, long adminRoleId = 10)
    {
        _userRepo.GetByEmailAsync(Arg.Any<string>()).Returns((User?)null);
        _passwordService.Hash(Arg.Any<string>()).Returns("hashed");
        _tenantRepo.SlugExistsAsync(Arg.Any<string>()).Returns(false);
        _uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        _userRepo.CreateAsync(Arg.Any<User>(), Arg.Any<IDbTransaction?>()).Returns(userId);
        _tenantRepo.CreateAsync(Arg.Any<Tenant>(), Arg.Any<IDbTransaction?>()).Returns(tenantId);
        _tenantRepo.CreateRoleAsync(Arg.Any<TenantRole>(), Arg.Any<IDbTransaction?>()).Returns(adminRoleId);
        _jwtService.GenerateToken(Arg.Any<User>(), tenantId, out Arg.Any<Guid>()).Returns("token");
        _userRepo.GetByIdAsync(userId).Returns(new User
        {
            Id = userId,
            PublicId = Guid.NewGuid(),
            Email = "test@example.com",
            Name = "Jane Doe",
        });
        _queryContext.IpAddress.Returns("127.0.0.1");
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_ReturnsTokenAndUserDetails()
    {
        SetupHappyPath();
        var sut = CreateSut();
        var command = new SignupCommand("test@example.com", "password123", "Jane Doe", "Acme Corp");

        var result = await sut.HandleAsync(command);

        result.Token.Should().Be("token");
        result.Email.Should().Be("test@example.com");
        result.TenantId.Should().Be(2);
    }

    [Fact]
    public async Task HandleAsync_DuplicateEmail_ThrowsDuplicateException()
    {
        _userRepo.GetByEmailAsync(Arg.Any<string>()).Returns(new User { Email = "taken@example.com" });
        var sut = CreateSut();
        var command = new SignupCommand("taken@example.com", "password123", "Jane Doe", "Acme Corp");

        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<DuplicateException>();
    }

    [Fact]
    public async Task HandleAsync_InvalidEmail_ThrowsValidationException()
    {
        var sut = CreateSut();
        var command = new SignupCommand("not-an-email", "password123", "Jane Doe", "Acme Corp");

        await sut.Invoking(s => s.HandleAsync(command))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task HandleAsync_SlugAlreadyTaken_AppendsCounter()
    {
        SetupHappyPath();
        _tenantRepo.SlugExistsAsync("acme-corp").Returns(true);
        _tenantRepo.SlugExistsAsync("acme-corp-1").Returns(false);
        var sut = CreateSut();

        await sut.HandleAsync(new SignupCommand("test@example.com", "password123", "Jane Doe", "Acme Corp"));

        await _tenantRepo.Received().CreateAsync(
            Arg.Is<Tenant>(t => t.Slug == "acme-corp-1"),
            Arg.Any<IDbTransaction?>());
    }

    [Fact]
    public async Task HandleAsync_RepoThrows_RollsBackTransaction()
    {
        _userRepo.GetByEmailAsync(Arg.Any<string>()).Returns((User?)null);
        _passwordService.Hash(Arg.Any<string>()).Returns("hashed");
        _tenantRepo.SlugExistsAsync(Arg.Any<string>()).Returns(false);
        _uow.Transaction.Returns(Substitute.For<IDbTransaction>());
        _userRepo.CreateAsync(Arg.Any<User>(), Arg.Any<IDbTransaction?>())
            .Returns(Task.FromException<long>(new Exception("db error")));
        var sut = CreateSut();

        await sut.Invoking(s => s.HandleAsync(new SignupCommand("test@example.com", "password123", "Jane Doe", "Acme")))
            .Should().ThrowAsync<Exception>();

        await _uow.Received().RollbackAsync(Arg.Any<CancellationToken>());
    }
}
