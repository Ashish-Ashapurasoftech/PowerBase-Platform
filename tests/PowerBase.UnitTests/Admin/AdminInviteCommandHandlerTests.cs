using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using PowerBase.Application.Admin;
using PowerBase.Application.Admin.Commands;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Admin;

public class AdminInviteCommandHandlerTests
{
    private readonly IAdminRepository _adminRepository = Substitute.For<IAdminRepository>();
    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IAuditRepository _auditRepository = Substitute.For<IAuditRepository>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    private readonly AdminInviteUserCommandHandler _inviteUserHandler;
    private readonly AdminInvitePlatformUserCommandHandler _invitePlatformUserHandler;

    public AdminInviteCommandHandlerTests()
    {
        _inviteUserHandler = new AdminInviteUserCommandHandler(
            _adminRepository,
            _userRepository,
            _tenantRepository,
            _auditRepository,
            _emailService
        );

        _invitePlatformUserHandler = new AdminInvitePlatformUserCommandHandler(
            _userRepository,
            _auditRepository,
            _emailService
        );
    }

    [Fact]
    public async Task InviteUser_NewUser_CreatesInactiveUserAssignsToTenantAndSendsInviteEmail()
    {
        // Arrange
        var rolePublicId = Guid.NewGuid();
        var command = new AdminInviteUserCommand(
            TenantId: 500,
            Email: "newuser@acme.com",
            RolePublicId: rolePublicId,
            FrontendBaseUrl: "http://frontend.com",
            InvitedByUserId: 1001
        );

        var createdUser = new User { Id = 2002, PublicId = Guid.NewGuid(), Email = "newuser@acme.com", IsActive = false, Name = "newuser" };
        var inviterUser = new User { Id = 1001, PublicId = Guid.NewGuid(), Email = "inviter@acme.com", Name = "Inviter Name" };

        _adminRepository.GetTenantRoleIdByPublicIdAsync(500, rolePublicId, Arg.Any<CancellationToken>())
            .Returns(88);

        _userRepository.GetByEmailAsync("newuser@acme.com", Arg.Any<CancellationToken>())
            .Returns((User?)null);

        _userRepository.CreateAsync(Arg.Any<User>(), null, Arg.Any<CancellationToken>())
            .Returns(2002);

        _userRepository.GetByIdAsync(2002, Arg.Any<CancellationToken>())
            .Returns(createdUser);

        _userRepository.GetByIdAsync(1001, Arg.Any<CancellationToken>())
            .Returns(inviterUser);

        _tenantRepository.GetTenantNameByIdAsync(500, Arg.Any<CancellationToken>())
            .Returns("Acme Tenant");

        // Act
        await _inviteUserHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _userRepository.Received(1).CreateAsync(Arg.Is<User>(u => 
            u.Email == "newuser@acme.com" && 
            !u.IsActive
        ), null, Arg.Any<CancellationToken>());

        await _adminRepository.Received(1).AssignUserToTenantAsync(500, 2002, 88, 1001, false, Arg.Any<CancellationToken>());

        await _auditRepository.Received(1).CreateInviteTokenAsync(
            2002,
            500,
            88,
            Arg.Any<string>(),
            Arg.Any<DateTime>(),
            1001,
            null,
            null,
            Arg.Any<CancellationToken>()
        );

        await _emailService.Received(1).SendInviteSetupEmailAsync(
            "newuser@acme.com",
            "Acme Tenant",
            "Inviter Name",
            Arg.Is<string>(s => s.StartsWith("http://frontend.com/auth/accept-invite?token=")),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task InviteUser_ExistingActiveUser_AssignsToTenantAndSendsInvitationEmail()
    {
        // Arrange
        var rolePublicId = Guid.NewGuid();
        var command = new AdminInviteUserCommand(
            TenantId: 500,
            Email: "existing@acme.com",
            RolePublicId: rolePublicId,
            FrontendBaseUrl: "http://frontend.com",
            InvitedByUserId: 1001
        );

        var existingUser = new User { Id = 3003, PublicId = Guid.NewGuid(), Email = "existing@acme.com", IsActive = true, Name = "existing" };
        var inviterUser = new User { Id = 1001, PublicId = Guid.NewGuid(), Email = "inviter@acme.com", Name = "Inviter Name" };

        _adminRepository.GetTenantRoleIdByPublicIdAsync(500, rolePublicId, Arg.Any<CancellationToken>())
            .Returns(88);

        _userRepository.GetByEmailAsync("existing@acme.com", Arg.Any<CancellationToken>())
            .Returns(existingUser);

        _adminRepository.ListTenantMembersAsync(500, Arg.Any<CancellationToken>())
            .Returns(new List<AdminTenantMemberDto>()); // not currently a member of this tenant

        _userRepository.GetByIdAsync(1001, Arg.Any<CancellationToken>())
            .Returns(inviterUser);

        _tenantRepository.GetTenantNameByIdAsync(500, Arg.Any<CancellationToken>())
            .Returns("Acme Tenant");

        // Act
        await _inviteUserHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _userRepository.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);

        await _adminRepository.Received(1).AssignUserToTenantAsync(500, 3003, 88, 1001, true, Arg.Any<CancellationToken>());

        await _auditRepository.DidNotReceiveWithAnyArgs().CreateInviteTokenAsync(default, default, default, default!, default, default, default, default, default);

        await _emailService.Received(1).SendInvitationEmailAsync(
            "existing@acme.com",
            "Acme Tenant",
            "Inviter Name",
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task InviteUser_AlreadyMember_ThrowsDuplicateException()
    {
        // Arrange
        var rolePublicId = Guid.NewGuid();
        var command = new AdminInviteUserCommand(
            TenantId: 500,
            Email: "already@acme.com",
            RolePublicId: rolePublicId,
            FrontendBaseUrl: "http://frontend.com",
            InvitedByUserId: 1001
        );

        var existingUser = new User { Id = 3003, PublicId = Guid.NewGuid(), Email = "already@acme.com", IsActive = true };

        _adminRepository.GetTenantRoleIdByPublicIdAsync(500, rolePublicId, Arg.Any<CancellationToken>())
            .Returns(88);

        _userRepository.GetByEmailAsync("already@acme.com", Arg.Any<CancellationToken>())
            .Returns(existingUser);

        var existingMembers = new List<AdminTenantMemberDto>
        {
            new AdminTenantMemberDto(existingUser.PublicId, "already@acme.com", "Name", true, rolePublicId, "Role", false, DateTime.UtcNow)
        };

        _adminRepository.ListTenantMembersAsync(500, Arg.Any<CancellationToken>())
            .Returns(existingMembers);

        // Act & Assert
        await Assert.ThrowsAsync<DuplicateException>(() => _inviteUserHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task InviteUser_EmptyEmail_ThrowsValidationException()
    {
        // Arrange
        var command = new AdminInviteUserCommand(500, "", Guid.NewGuid(), "url", 1001);

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() => _inviteUserHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task InviteUser_RoleNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var rolePublicId = Guid.NewGuid();
        var command = new AdminInviteUserCommand(500, "email@email.com", rolePublicId, "url", 1001);

        _adminRepository.GetTenantRoleIdByPublicIdAsync(500, rolePublicId, Arg.Any<CancellationToken>())
            .Returns((long?)null);

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() => _inviteUserHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task InvitePlatformUser_NewUser_CreatesInactiveUserAndSendsInviteEmail()
    {
        // Arrange
        var command = new AdminInvitePlatformUserCommand(
            Email: "platform@acme.com",
            FrontendBaseUrl: "http://frontend.com",
            InvitedByUserId: 1001
        );

        var createdUser = new User { Id = 2002, PublicId = Guid.NewGuid(), Email = "platform@acme.com", IsActive = false, Name = "platform" };
        var inviterUser = new User { Id = 1001, PublicId = Guid.NewGuid(), Email = "inviter@acme.com", Name = "Inviter Name" };

        _userRepository.GetByEmailAsync("platform@acme.com", Arg.Any<CancellationToken>())
            .Returns((User?)null);

        _userRepository.CreateAsync(Arg.Any<User>(), null, Arg.Any<CancellationToken>())
            .Returns(2002);

        _userRepository.GetByIdAsync(2002, Arg.Any<CancellationToken>())
            .Returns(createdUser);

        _userRepository.GetByIdAsync(1001, Arg.Any<CancellationToken>())
            .Returns(inviterUser);

        // Act
        await _invitePlatformUserHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _userRepository.Received(1).CreateAsync(Arg.Is<User>(u => 
            u.Email == "platform@acme.com" && 
            !u.IsActive
        ), null, Arg.Any<CancellationToken>());

        await _auditRepository.Received(1).CreateInviteTokenAsync(
            2002,
            null,
            null,
            Arg.Any<string>(),
            Arg.Any<DateTime>(),
            1001,
            null,
            null,
            Arg.Any<CancellationToken>()
        );

        await _emailService.Received(1).SendInviteSetupEmailAsync(
            "platform@acme.com",
            "PowerBase",
            "Inviter Name",
            Arg.Is<string>(s => s.StartsWith("http://frontend.com/auth/accept-invite?token=")),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task InvitePlatformUser_ExistingInactiveUser_SendsInviteEmail()
    {
        // Arrange
        var command = new AdminInvitePlatformUserCommand(
            Email: "platform-inactive@acme.com",
            FrontendBaseUrl: "http://frontend.com",
            InvitedByUserId: 1001
        );

        var existingUser = new User { Id = 4004, PublicId = Guid.NewGuid(), Email = "platform-inactive@acme.com", IsActive = false, Name = "platform-inactive" };
        var inviterUser = new User { Id = 1001, PublicId = Guid.NewGuid(), Email = "inviter@acme.com", Name = "Inviter Name" };

        _userRepository.GetByEmailAsync("platform-inactive@acme.com", Arg.Any<CancellationToken>())
            .Returns(existingUser);

        _userRepository.GetByIdAsync(1001, Arg.Any<CancellationToken>())
            .Returns(inviterUser);

        // Act
        await _invitePlatformUserHandler.HandleAsync(command, CancellationToken.None);

        // Assert
        await _userRepository.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);

        await _auditRepository.Received(1).CreateInviteTokenAsync(
            4004,
            null,
            null,
            Arg.Any<string>(),
            Arg.Any<DateTime>(),
            1001,
            null,
            null,
            Arg.Any<CancellationToken>()
        );

        await _emailService.Received(1).SendInviteSetupEmailAsync(
            "platform-inactive@acme.com",
            "PowerBase",
            "Inviter Name",
            Arg.Is<string>(s => s.StartsWith("http://frontend.com/auth/accept-invite?token=")),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task InvitePlatformUser_ExistingActiveUser_ThrowsDuplicateException()
    {
        // Arrange
        var command = new AdminInvitePlatformUserCommand("active@acme.com", "url", 1001);
        var existingUser = new User { Id = 5005, Email = "active@acme.com", IsActive = true };

        _userRepository.GetByEmailAsync("active@acme.com", Arg.Any<CancellationToken>())
            .Returns(existingUser);

        // Act & Assert
        await Assert.ThrowsAsync<DuplicateException>(() => _invitePlatformUserHandler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task InvitePlatformUser_EmptyEmail_ThrowsValidationException()
    {
        // Arrange
        var command = new AdminInvitePlatformUserCommand("", "url", 1001);

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(() => _invitePlatformUserHandler.HandleAsync(command, CancellationToken.None));
    }
}
