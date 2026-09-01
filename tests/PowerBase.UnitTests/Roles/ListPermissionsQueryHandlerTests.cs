using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Roles.Queries.ListPermissions;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Roles;

public class ListPermissionsQueryHandlerTests
{
    private readonly IPermissionRepository _permissionRepo = Substitute.For<IPermissionRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private ListPermissionsQueryHandler CreateSut() => new(_permissionRepo, _queryContext);

    private static List<Permission> MakePermissions() =>
    [
        new Permission { Id = 1, Code = PermissionCodes.AppsRead, DisplayName = "View Apps" },
        new Permission { Id = 2, Code = PermissionCodes.AppsCreate, DisplayName = "Create Apps" },
        new Permission { Id = 3, Code = PermissionCodes.RolesManage, DisplayName = "Manage Roles" },
        new Permission { Id = 4, Code = PermissionCodes.UsersManage, DisplayName = "Manage Users" }
    ];

    [Fact]
    public async Task HandleAsync_SuperAdmin_ReturnsAllPermissions()
    {
        var allPerms = MakePermissions();
        _permissionRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allPerms);
        _queryContext.IsSuperAdmin.Returns(true);
        _queryContext.IsTenantAdmin.Returns(false);

        var sut = CreateSut();
        var result = await sut.HandleAsync(new ListPermissionsQuery());

        result.Should().HaveCount(4);
        result.Select(p => p.Code).Should().BeEquivalentTo([PermissionCodes.AppsRead, PermissionCodes.AppsCreate, PermissionCodes.RolesManage, PermissionCodes.UsersManage]);
    }

    [Fact]
    public async Task HandleAsync_TenantAdmin_ReturnsAllPermissions()
    {
        var allPerms = MakePermissions();
        _permissionRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allPerms);
        _queryContext.IsSuperAdmin.Returns(false);
        _queryContext.IsTenantAdmin.Returns(true);

        var sut = CreateSut();
        var result = await sut.HandleAsync(new ListPermissionsQuery());

        result.Should().HaveCount(4);
    }

    [Fact]
    public async Task HandleAsync_ManagerRole_ReturnsOnlyPermissionsPossessedByManager()
    {
        var allPerms = MakePermissions();
        _permissionRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(allPerms);
        _queryContext.IsSuperAdmin.Returns(false);
        _queryContext.IsTenantAdmin.Returns(false);
        _queryContext.Permissions.Returns(new HashSet<string> { PermissionCodes.AppsRead, PermissionCodes.RolesManage });

        var sut = CreateSut();
        var result = await sut.HandleAsync(new ListPermissionsQuery());

        result.Should().HaveCount(2);
        result.Select(p => p.Code).Should().BeEquivalentTo([PermissionCodes.AppsRead, PermissionCodes.RolesManage]);
    }
}
