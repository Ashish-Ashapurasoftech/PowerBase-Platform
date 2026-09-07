using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Roles.Commands.CreateRole;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Roles;

public class CreateRoleCommandHandlerTests
{
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IPermissionRepository _permissionRepo = Substitute.For<IPermissionRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private CreateRoleCommandHandler CreateSut() => new(_tenantRepo, _permissionRepo, _queryContext, _auditRepo);

    private static List<Permission> MakePermissions() =>
    [
        new Permission { Id = 1, Code = PermissionCodes.AppsRead, DisplayName = "View Apps" },
        new Permission { Id = 2, Code = PermissionCodes.AppsCreate, DisplayName = "Create Apps" },
        new Permission { Id = 3, Code = PermissionCodes.RolesManage, DisplayName = "Manage Roles" },
        new Permission { Id = 4, Code = PermissionCodes.UsersManage, DisplayName = "Manage Users" }
    ];

    [Fact]
    public async Task HandleAsync_ManagerWithSubsetPermissions_CreatesRoleSuccessfully()
    {
        var allPerms = MakePermissions();
        _permissionRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allPerms);
        _tenantRepo.RoleNameExistsAsync("Support", Arg.Any<CancellationToken>()).Returns(false);
        _queryContext.IsSuperAdmin.Returns(false);
        _queryContext.IsTenantAdmin.Returns(false);
        _queryContext.TenantId.Returns(10);
        _queryContext.UserId.Returns(5);
        _queryContext.Permissions.Returns(new HashSet<string> { PermissionCodes.AppsRead, PermissionCodes.RolesManage });

        _tenantRepo.CreateRoleAsync(Arg.Any<TenantRole>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>()).Returns(100L);
        _tenantRepo.GetRoleByIdAsync(100L, Arg.Any<CancellationToken>()).Returns(new TenantRole
        {
            Id = 100,
            PublicId = Guid.NewGuid(),
            Name = "Support",
            Description = "Support team",
            IsDefault = false,
            IsSystem = false,
            CreatedOn = DateTime.UtcNow
        });

        var sut = CreateSut();
        var result = await sut.HandleAsync(new CreateRoleCommand("Support", "Support team", [PermissionCodes.AppsRead]));

        result.Should().NotBeNull();
        result.Name.Should().Be("Support");
        await _permissionRepo.Received(1).AssignToRoleAsync(100L, Arg.Is<IReadOnlyList<long>>(p => p.Contains(1L)), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ManagerAssigningUnpossessedPermission_ThrowsValidationException()
    {
        var allPerms = MakePermissions();
        _permissionRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allPerms);
        _tenantRepo.RoleNameExistsAsync("CustomRole", Arg.Any<CancellationToken>()).Returns(false);
        _queryContext.IsSuperAdmin.Returns(false);
        _queryContext.IsTenantAdmin.Returns(false);
        _queryContext.Permissions.Returns(new HashSet<string> { PermissionCodes.AppsRead, PermissionCodes.RolesManage });

        var sut = CreateSut();
        // Trying to grant UsersManage which the Manager does not have
        var command = new CreateRoleCommand("CustomRole", "Desc", [PermissionCodes.AppsRead, PermissionCodes.UsersManage]);

        var act = () => sut.HandleAsync(command);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().ContainKey("PermissionCodes");
        ex.Which.Errors["PermissionCodes"][0].Should().Contain("You cannot assign permissions that your own role does not possess");
    }

    [Fact]
    public async Task HandleAsync_SuperAdminAssigningAnyValidPermission_Succeeds()
    {
        var allPerms = MakePermissions();
        _permissionRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allPerms);
        _tenantRepo.RoleNameExistsAsync("AdminRole", Arg.Any<CancellationToken>()).Returns(false);
        _queryContext.IsSuperAdmin.Returns(true);
        _queryContext.IsTenantAdmin.Returns(false);
        _queryContext.Permissions.Returns(new HashSet<string>()); // Empty permissions set on context, but SuperAdmin bypasses

        _tenantRepo.CreateRoleAsync(Arg.Any<TenantRole>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>()).Returns(200L);
        _tenantRepo.GetRoleByIdAsync(200L, Arg.Any<CancellationToken>()).Returns(new TenantRole
        {
            Id = 200,
            PublicId = Guid.NewGuid(),
            Name = "AdminRole",
            IsDefault = false,
            IsSystem = false,
            CreatedOn = DateTime.UtcNow
        });

        var sut = CreateSut();
        var result = await sut.HandleAsync(new CreateRoleCommand("AdminRole", "Desc", [PermissionCodes.UsersManage]));

        result.Should().NotBeNull();
        result.Name.Should().Be("AdminRole");
    }
}
