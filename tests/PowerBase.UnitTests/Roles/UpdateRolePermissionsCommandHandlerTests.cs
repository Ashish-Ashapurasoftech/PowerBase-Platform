using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Roles.Commands.UpdateRolePermissions;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Roles;

public class UpdateRolePermissionsCommandHandlerTests
{
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IPermissionRepository _permissionRepo = Substitute.For<IPermissionRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private UpdateRolePermissionsCommandHandler CreateSut() => new(_tenantRepo, _permissionRepo, _auditRepo, _queryContext);

    private static List<Permission> MakePermissions() =>
    [
        new Permission { Id = 1, Code = PermissionCodes.AppsRead, DisplayName = "View Apps" },
        new Permission { Id = 2, Code = PermissionCodes.AppsCreate, DisplayName = "Create Apps" },
        new Permission { Id = 3, Code = PermissionCodes.RolesManage, DisplayName = "Manage Roles" },
        new Permission { Id = 4, Code = PermissionCodes.UsersManage, DisplayName = "Manage Users" }
    ];

    [Fact]
    public async Task HandleAsync_ManagerWithSubsetPermissions_UpdatesSuccessfully()
    {
        var rolePublicId = Guid.NewGuid();
        var targetRole = new TenantRole { Id = 50, PublicId = rolePublicId, Name = "Analyst" };
        _tenantRepo.GetRoleByPublicIdAsync(rolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);

        var allPerms = MakePermissions();
        _permissionRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allPerms);

        _queryContext.TenantRole.Returns("Manager");
        _queryContext.IsSuperAdmin.Returns(false);
        _queryContext.IsTenantAdmin.Returns(false);
        _queryContext.Permissions.Returns(new HashSet<string> { PermissionCodes.AppsRead, PermissionCodes.RolesManage });

        var sut = CreateSut();
        await sut.HandleAsync(new UpdateRolePermissionsCommand(rolePublicId, [PermissionCodes.AppsRead]));

        await _permissionRepo.Received(1).ReplaceRolePermissionsAsync(50L, Arg.Is<IReadOnlyList<long>>(p => p.Count == 1 && p[0] == 1L), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ManagerAssigningUnpossessedPermission_ThrowsValidationException()
    {
        var rolePublicId = Guid.NewGuid();
        var targetRole = new TenantRole { Id = 50, PublicId = rolePublicId, Name = "Analyst" };
        _tenantRepo.GetRoleByPublicIdAsync(rolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);

        var allPerms = MakePermissions();
        _permissionRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allPerms);

        _queryContext.TenantRole.Returns("Manager");
        _queryContext.IsSuperAdmin.Returns(false);
        _queryContext.IsTenantAdmin.Returns(false);
        _queryContext.Permissions.Returns(new HashSet<string> { PermissionCodes.AppsRead, PermissionCodes.RolesManage });

        var sut = CreateSut();
        // Trying to grant UsersManage which Manager lacks
        var act = () => sut.HandleAsync(new UpdateRolePermissionsCommand(rolePublicId, [PermissionCodes.AppsRead, PermissionCodes.UsersManage]));

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().ContainKey("PermissionCodes");
        ex.Which.Errors["PermissionCodes"][0].Should().Contain("You cannot assign permissions that your own role does not possess");
    }

    [Fact]
    public async Task HandleAsync_CallerModifyingOwnRole_ThrowsUnauthorizedActionException()
    {
        var rolePublicId = Guid.NewGuid();
        var targetRole = new TenantRole { Id = 50, PublicId = rolePublicId, Name = "Manager" };
        _tenantRepo.GetRoleByPublicIdAsync(rolePublicId, Arg.Any<CancellationToken>()).Returns(targetRole);

        _queryContext.TenantRole.Returns("Manager");

        var sut = CreateSut();
        var act = () => sut.HandleAsync(new UpdateRolePermissionsCommand(rolePublicId, [PermissionCodes.AppsRead]));

        await act.Should().ThrowAsync<UnauthorizedActionException>();
    }
}
