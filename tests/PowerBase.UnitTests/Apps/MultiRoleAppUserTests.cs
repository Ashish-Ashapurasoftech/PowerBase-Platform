using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.AddAppUser;
using PowerBase.Application.Apps.Commands.ChangeAppUserRole;
using PowerBase.Application.Apps.Commands.InviteAppUser;
using PowerBase.Application.Apps.Commands.RemoveAppUser;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Apps;

public class MultiRoleAppUserTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    public MultiRoleAppUserTests()
    {
        _queryContext.UserId.Returns(100L);
        _queryContext.TenantId.Returns(1L);
        _queryContext.IsSuperAdmin.Returns(true);
    }

    [Fact]
    public async Task AddAppUser_AllowsAddingSameUserWithDifferentRoles()
    {
        // Arrange
        var appId = 10L;
        var appPublicId = Guid.NewGuid();
        var role1PublicId = Guid.NewGuid();
        var role2PublicId = Guid.NewGuid();

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _userRepo.GetByEmailAsync("john@example.com", Arg.Any<CancellationToken>()).Returns(new User
        {
            Id = 50L,
            PublicId = Guid.NewGuid(),
            Name = "John Doe",
            Email = "john@example.com"
        });

        _appRoleRepo.GetByPublicIdAsync(role1PublicId, Arg.Any<CancellationToken>())
            .Returns(new AppRole { Id = 1, AppId = appId, PublicId = role1PublicId, Name = "Participant" });
        _appRoleRepo.GetByPublicIdAsync(role2PublicId, Arg.Any<CancellationToken>())
            .Returns(new AppRole { Id = 2, AppId = appId, PublicId = role2PublicId, Name = "Administrator" });

        var sut = new AddAppUserCommandHandler(_appRepo, _appRoleRepo, _appUserRepo, _userRepo, _appAccessService, _queryContext, _auditRepo);

        // Act 1: Add user with Role 1 (Participant)
        await sut.HandleAsync(new AddAppUserCommand(appPublicId, "john@example.com", role1PublicId), CancellationToken.None);

        // Act 2: Add same user with Role 2 (Administrator)
        await sut.HandleAsync(new AddAppUserCommand(appPublicId, "john@example.com", role2PublicId), CancellationToken.None);

        // Assert: 2 separate CreateAsync calls should have been made
        await _appUserRepo.Received(2).CreateAsync(Arg.Any<AppUser>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddAppUser_SameUserWithSameRole_ThrowsDuplicateException()
    {
        // Arrange
        var appId = 10L;
        var appPublicId = Guid.NewGuid();
        var role1PublicId = Guid.NewGuid();

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _userRepo.GetByEmailAsync("john@example.com", Arg.Any<CancellationToken>()).Returns(new User
        {
            Id = 50L,
            PublicId = Guid.NewGuid(),
            Name = "John Doe",
            Email = "john@example.com"
        });

        _appRoleRepo.GetByPublicIdAsync(role1PublicId, Arg.Any<CancellationToken>())
            .Returns(new AppRole { Id = 1, AppId = appId, PublicId = role1PublicId, Name = "Participant" });

        // Simulate user already has Role 1
        _appUserRepo.GetByAppUserAndRoleAsync(appId, 50L, 1L, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = 99L, AppId = appId, UserId = 50L, AppRoleId = 1L });

        var sut = new AddAppUserCommandHandler(_appRepo, _appRoleRepo, _appUserRepo, _userRepo, _appAccessService, _queryContext, _auditRepo);

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(new AddAppUserCommand(appPublicId, "john@example.com", role1PublicId), CancellationToken.None))
            .Should().ThrowAsync<DuplicateException>()
            .WithMessage("*already has the 'Participant' role*");
    }

    [Fact]
    public async Task InviteAppUser_SameUserWithSameRole_ThrowsDuplicateException()
    {
        // Arrange
        var appId = 10L;
        var appPublicId = Guid.NewGuid();
        var role1PublicId = Guid.NewGuid();

        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(appId);
        _userRepo.GetByEmailAsync("john@example.com", Arg.Any<CancellationToken>()).Returns(new User
        {
            Id = 50L,
            PublicId = Guid.NewGuid(),
            Name = "John Doe",
            Email = "john@example.com",
            IsActive = true
        });

        _userRepo.GetByIdAsync(100L, Arg.Any<CancellationToken>()).Returns(new User { Id = 100L, Name = "Admin" });
        _tenantRepo.IsActiveMemberAsync(50L, Arg.Any<CancellationToken>()).Returns(true);

        _appRoleRepo.GetByPublicIdAsync(role1PublicId, Arg.Any<CancellationToken>())
            .Returns(new AppRole { Id = 1, AppId = appId, PublicId = role1PublicId, Name = "Participant" });

        // Simulate user already has Role 1
        _appUserRepo.GetByAppUserAndRoleAsync(appId, 50L, 1L, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = 99L, AppId = appId, UserId = 50L, AppRoleId = 1L });

        var sut = new InviteAppUserCommandHandler(
            _appRepo, _appRoleRepo, _appUserRepo, _userRepo, _tenantRepo, _auditRepo, _emailService, _queryContext);

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(new InviteAppUserCommand(appPublicId, "john@example.com", role1PublicId, "http://localhost:4200"), CancellationToken.None))
            .Should().ThrowAsync<DuplicateException>()
            .WithMessage("*already has the 'Participant' role*");
    }

    [Fact]
    public async Task RemoveAppUser_ByAssignmentPublicId_CallsRemoveAssignment()
    {
        // Arrange
        var appId = 10L;
        var appPublicId = Guid.NewGuid();
        var assignmentPublicId = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(new App { Id = appId, OwnerId = 1L });
        _appUserRepo.GetByPublicIdAsync(assignmentPublicId, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = 999L,
            PublicId = assignmentPublicId,
            AppId = appId,
            UserId = 50L,
            UserEmail = "john@example.com",
            AppRoleId = 1L
        });

        var sut = new RemoveAppUserCommandHandler(_appRepo, _appUserRepo, _queryContext, _auditRepo);

        // Act
        await sut.HandleAsync(new RemoveAppUserCommand(appPublicId, assignmentPublicId), CancellationToken.None);

        // Assert: RemoveAssignmentAsync was called with the specific assignment PublicId
        await _appUserRepo.Received(1).RemoveAssignmentAsync(appId, assignmentPublicId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeAppUserRole_SameRole_ThrowsBadRequestException()
    {
        // Arrange
        var appId = 10L;
        var appPublicId = Guid.NewGuid();
        var assignmentPublicId = Guid.NewGuid();
        var role1PublicId = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(new App { Id = appId, OwnerId = 1L });
        _appRoleRepo.GetByPublicIdAsync(role1PublicId, Arg.Any<CancellationToken>())
            .Returns(new AppRole { Id = 1, AppId = appId, PublicId = role1PublicId, Name = "Participant" });

        _appUserRepo.GetByPublicIdAsync(assignmentPublicId, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = 999L,
            PublicId = assignmentPublicId,
            AppId = appId,
            UserId = 50L,
            UserEmail = "john@example.com",
            AppRoleId = 1L
        });

        var sut = new ChangeAppUserRoleCommandHandler(
            _appRepo, _appRoleRepo, _appUserRepo, _userRepo, _appAccessService, _queryContext, _auditRepo);

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(new ChangeAppUserRoleCommand(appPublicId, assignmentPublicId, role1PublicId), CancellationToken.None))
            .Should().ThrowAsync<BadRequestException>()
            .WithMessage("*already assigned the 'Participant' role*");
    }

    [Fact]
    public async Task ChangeAppUserRole_TargetRoleExistsOnOtherAssignment_ThrowsDuplicateException()
    {
        // Arrange
        var appId = 10L;
        var appPublicId = Guid.NewGuid();
        var assignmentPublicId = Guid.NewGuid();
        var role2PublicId = Guid.NewGuid();

        _appRepo.GetByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(new App { Id = appId, OwnerId = 1L });
        _appRoleRepo.GetByPublicIdAsync(role2PublicId, Arg.Any<CancellationToken>())
            .Returns(new AppRole { Id = 2, AppId = appId, PublicId = role2PublicId, Name = "Administrator" });

        _appUserRepo.GetByPublicIdAsync(assignmentPublicId, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = 999L,
            PublicId = assignmentPublicId,
            AppId = appId,
            UserId = 50L,
            UserEmail = "john@example.com",
            AppRoleId = 1L // Currently Participant
        });

        // User already has Administrator (RoleId 2) on another assignment (Id 888L)
        _appUserRepo.GetByAppUserAndRoleAsync(appId, 50L, 2L, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = 888L, AppId = appId, UserId = 50L, AppRoleId = 2L });

        var sut = new ChangeAppUserRoleCommandHandler(
            _appRepo, _appRoleRepo, _appUserRepo, _userRepo, _appAccessService, _queryContext, _auditRepo);

        // Act & Assert
        await sut.Invoking(s => s.HandleAsync(new ChangeAppUserRoleCommand(appPublicId, assignmentPublicId, role2PublicId), CancellationToken.None))
            .Should().ThrowAsync<DuplicateException>()
            .WithMessage("*already has the 'Administrator' role*");
    }
}
