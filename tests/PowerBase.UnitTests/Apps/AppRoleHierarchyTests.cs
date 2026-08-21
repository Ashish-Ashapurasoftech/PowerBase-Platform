using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.CreateAppRole;
using PowerBase.Application.Apps.Commands.UpdateAppRole;
using PowerBase.Application.Apps.Commands.DeleteAppRole;
using PowerBase.Application.Apps.Commands.AddAppUser;
using PowerBase.Application.Apps.Commands.InviteAppUser;
using PowerBase.Application.Apps.Commands.ChangeAppUserRole;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PowerBase.UnitTests.Apps;

public class AppRoleHierarchyTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IAppRolePermissionRepository _permRepo = Substitute.For<IAppRolePermissionRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();

    public AppRoleHierarchyTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(100L); // Actor UserId
    }

    [Fact]
    public async Task CreateAppRole_NonSuperAdminSpecifiesHierarchySettings_ThrowsUnauthorizedActionException()
    {
        _queryContext.IsSuperAdmin.Returns(false);
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });
        var sut = new CreateAppRoleCommandHandler(_appRepo, _appRoleRepo, _queryContext, _auditRepo, _permRepo, _appUserRepo);
        var command = new CreateAppRoleCommand(Guid.NewGuid(), "Custom Role", false, "Below", 2, null);

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task CreateAppRole_InvalidManageableRolesType_ThrowsValidationException()
    {
        _queryContext.IsSuperAdmin.Returns(true);
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });
        var sut = new CreateAppRoleCommandHandler(_appRepo, _appRoleRepo, _queryContext, _auditRepo, _permRepo, _appUserRepo);
        var command = new CreateAppRoleCommand(Guid.NewGuid(), "Custom Role", false, "InvalidType", 2, null);

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAppRole_NegativeRank_ThrowsValidationException()
    {
        _queryContext.IsSuperAdmin.Returns(true);
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });
        var sut = new CreateAppRoleCommandHandler(_appRepo, _appRoleRepo, _queryContext, _auditRepo, _permRepo, _appUserRepo);
        var command = new CreateAppRoleCommand(Guid.NewGuid(), "Custom Role", false, "Below", -1, null);

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateAppRole_InvalidManageableRolesType_ThrowsValidationException()
    {
        var targetRolePublicId = Guid.NewGuid();
        var targetRole = new AppRole { Id = 2, AppId = 10, PublicId = targetRolePublicId, Name = "Viewer", Rank = 3 };
        _appRoleRepo.GetByPublicIdAsync(targetRolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });

        var sut = new UpdateAppRoleCommandHandler(_appRoleRepo, _appAccessService, _auditRepo, _queryContext, _appUserRepo, _appRepo);
        var command = new UpdateAppRoleCommand(Guid.NewGuid(), targetRolePublicId, null, "InvalidType", 2, null);

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateAppRole_NegativeRank_ThrowsValidationException()
    {
        var targetRolePublicId = Guid.NewGuid();
        var targetRole = new AppRole { Id = 2, AppId = 10, PublicId = targetRolePublicId, Name = "Viewer", Rank = 3 };
        _appRoleRepo.GetByPublicIdAsync(targetRolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });

        var sut = new UpdateAppRoleCommandHandler(_appRoleRepo, _appAccessService, _auditRepo, _queryContext, _appUserRepo, _appRepo);
        var command = new UpdateAppRoleCommand(Guid.NewGuid(), targetRolePublicId, null, "Below", -1, null);

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAppRole_SuperAdminSpecifiesEqualOrSuperiorRankRole_ThrowsValidationException()
    {
        _queryContext.IsSuperAdmin.Returns(true);
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });
        var superiorRolePublicId = Guid.NewGuid();
        var superiorRole = new AppRole { Id = 1, PublicId = superiorRolePublicId, Name = "Admin", Rank = 2 };

        _appRoleRepo.GetByPublicIdAsync(superiorRolePublicId, Arg.Any<CancellationToken>())
            .Returns(superiorRole);

        var sut = new CreateAppRoleCommandHandler(_appRepo, _appRoleRepo, _queryContext, _auditRepo, _permRepo, _appUserRepo);
        var command = new CreateAppRoleCommand(Guid.NewGuid(), "Custom Role", false, "Manual", 3, new List<Guid> { superiorRolePublicId });

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpdateAppRole_NonSuperAdminConfiguresHierarchy_ThrowsUnauthorizedActionException()
    {
        _queryContext.IsSuperAdmin.Returns(false);
        var targetRolePublicId = Guid.NewGuid();
        var targetRole = new AppRole { Id = 2, AppId = 10, PublicId = targetRolePublicId, Name = "Viewer", Rank = 3 };

        _appRoleRepo.GetByPublicIdAsync(targetRolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);

        var actorRolePublicId = Guid.NewGuid();
        var actorRole = new AppRole { Id = 1, AppId = 10, PublicId = actorRolePublicId, Name = "Admin", Rank = 2, ManageableRolesType = "Below" };

        _appUserRepo.GetUserRolePublicIdAsync(10L, 100L, Arg.Any<CancellationToken>()).Returns(actorRolePublicId);
        _appRoleRepo.GetByPublicIdAsync(actorRolePublicId, Arg.Any<CancellationToken>()).Returns(actorRole);
        _appUserRepo.GetByAppAndUserAsync(10L, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser());
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });

        var sut = new UpdateAppRoleCommandHandler(_appRoleRepo, _appAccessService, _auditRepo, _queryContext, _appUserRepo, _appRepo);
        var command = new UpdateAppRoleCommand(Guid.NewGuid(), targetRolePublicId, null, "Below", 2, null);

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task UpdateAppRole_NonSuperAdminAttemptsToEditSuperiorRole_ThrowsUnauthorizedActionException()
    {
        _queryContext.IsSuperAdmin.Returns(false);
        var targetRolePublicId = Guid.NewGuid();
        var targetRole = new AppRole { Id = 1, AppId = 10, PublicId = targetRolePublicId, Name = "Admin", Rank = 2 };

        _appRoleRepo.GetByPublicIdAsync(targetRolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);

        var actorRolePublicId = Guid.NewGuid();
        var actorRole = new AppRole { Id = 2, AppId = 10, PublicId = actorRolePublicId, Name = "Viewer", Rank = 3 };

        _appUserRepo.GetUserRolePublicIdAsync(10L, 100L, Arg.Any<CancellationToken>()).Returns(actorRolePublicId);
        _appRoleRepo.GetByPublicIdAsync(actorRolePublicId, Arg.Any<CancellationToken>()).Returns(actorRole);
        _appUserRepo.GetByAppAndUserAsync(10L, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser());
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });

        var sut = new UpdateAppRoleCommandHandler(_appRoleRepo, _appAccessService, _auditRepo, _queryContext, _appUserRepo, _appRepo);
        var command = new UpdateAppRoleCommand(Guid.NewGuid(), targetRolePublicId, new List<string> { "permissions:read" });

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task DeleteAppRole_NonSuperAdminAttemptsToDeleteSuperiorRole_ThrowsUnauthorizedActionException()
    {
        _queryContext.IsSuperAdmin.Returns(false);
        var targetRolePublicId = Guid.NewGuid();
        var targetRole = new AppRole { Id = 1, AppId = 10, PublicId = targetRolePublicId, Name = "Admin", Rank = 2, IsSystem = false };

        _appRoleRepo.GetByPublicIdAsync(targetRolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);

        var actorRolePublicId = Guid.NewGuid();
        var actorRole = new AppRole { Id = 2, AppId = 10, PublicId = actorRolePublicId, Name = "Viewer", Rank = 3 };

        _appUserRepo.GetUserRolePublicIdAsync(10L, 100L, Arg.Any<CancellationToken>()).Returns(actorRolePublicId);
        _appRoleRepo.GetByPublicIdAsync(actorRolePublicId, Arg.Any<CancellationToken>()).Returns(actorRole);
        _appUserRepo.GetByAppAndUserAsync(10L, 100L, Arg.Any<CancellationToken>()).Returns(new AppUser());

        var sut = new DeleteAppRoleCommandHandler(_appRoleRepo, _auditRepo, _queryContext, _appUserRepo);
        var command = new DeleteAppRoleCommand(Guid.NewGuid(), targetRolePublicId);

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task AddAppUser_NonSuperAdminAssignsEqualOrSuperiorRole_ThrowsUnauthorizedActionException()
    {
        _queryContext.IsSuperAdmin.Returns(false);
        var targetRolePublicId = Guid.NewGuid();
        var targetRole = new AppRole { Id = 1, AppId = 10, PublicId = targetRolePublicId, Name = "Admin", Rank = 2 };

        _appRoleRepo.GetByPublicIdAsync(targetRolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);

        var actorRolePublicId = Guid.NewGuid();
        var actorRole = new AppRole { Id = 2, AppId = 10, PublicId = actorRolePublicId, Name = "Viewer", Rank = 3 };

        _appUserRepo.GetUserRolePublicIdAsync(10L, 100L, Arg.Any<CancellationToken>()).Returns(actorRolePublicId);
        _appRoleRepo.GetByPublicIdAsync(actorRolePublicId, Arg.Any<CancellationToken>()).Returns(actorRole);
        _appRepo.GetIdByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(10L);
        _userRepo.GetByEmailAsync("newuser@example.com", Arg.Any<CancellationToken>()).Returns(new User { Id = 50 });

        var sut = new AddAppUserCommandHandler(_appRepo, _appRoleRepo, _appUserRepo, _userRepo, _appAccessService, _queryContext, _auditRepo);
        var command = new AddAppUserCommand(Guid.NewGuid(), "newuser@example.com", targetRolePublicId);

        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task ChangeAppUserRole_NonSuperAdminManagesUserWithEqualOrSuperiorRole_ThrowsUnauthorizedActionException()
    {
        _queryContext.IsSuperAdmin.Returns(false);
        _appRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new App { Id = 10, OwnerId = 1 });
        _userRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new User { Id = 5 });

        // Target user currently has Administrator role (Rank 1)
        var targetUserAppUser = new AppUser { AppRoleId = 1 };
        _appUserRepo.GetByAppAndUserAsync(10L, 5L, Arg.Any<CancellationToken>()).Returns(targetUserAppUser);

        // Requested new role is Viewer (Rank 3)
        var targetRolePublicId = Guid.NewGuid();
        var targetRole = new AppRole { Id = 3, AppId = 10, PublicId = targetRolePublicId, Name = "Viewer", Rank = 3 };
        _appRoleRepo.GetByPublicIdAsync(targetRolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);

        // Actor has Administrator role (Rank 1)
        var actorRolePublicId = Guid.NewGuid();
        var actorRole = new AppRole { Id = 1, AppId = 10, PublicId = actorRolePublicId, Name = "Administrator", Rank = 1, ManageableRolesType = "Below" };
        _appUserRepo.GetUserRolePublicIdAsync(10L, 100L, Arg.Any<CancellationToken>()).Returns(actorRolePublicId);
        _appRoleRepo.GetByPublicIdAsync(actorRolePublicId, Arg.Any<CancellationToken>()).Returns(actorRole);

        // Load roles detail to resolve user's current role rank
        var rolesList = new List<AppRoleDetail>
        {
            new(1L, Guid.NewGuid(), 10L, "Administrator", true, true, Array.Empty<string>(), "Below", 1, Array.Empty<Guid>()),
            new(3L, Guid.NewGuid(), 10L, "Viewer", false, true, Array.Empty<string>(), "None", 3, Array.Empty<Guid>())
        };
        _appRoleRepo.ListDetailsByAppIdAsync(10L, Arg.Any<CancellationToken>()).Returns(rolesList);

        var sut = new ChangeAppUserRoleCommandHandler(_appRepo, _appRoleRepo, _appUserRepo, _userRepo, _appAccessService, _queryContext, _auditRepo);
        var command = new ChangeAppUserRoleCommand(Guid.NewGuid(), Guid.NewGuid(), targetRolePublicId);

        // Actor (Rank 1) cannot manage target user's current role which is Rank 1 (equal rank)
        await sut.Invoking(s => s.HandleAsync(command, CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }
}
