using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;
using PowerBase.Application.Groups.Queries.GetGroup;
using PowerBase.Application.Groups.Queries.GetSharedApps;
using PowerBase.Application.Groups.Queries.GetUserEffectivePermissions;
using PowerBase.Application.Groups.Queries.ListGroupMembers;
using PowerBase.Application.Groups.Queries.ListGroups;
using Xunit;

namespace PowerBase.UnitTests.Groups;

public class GroupQueryHandlerTests
{
    private readonly IGroupRepository _groupRepository = Substitute.For<IGroupRepository>();
    private readonly IAppUserRepository _appUserRepository = Substitute.For<IAppUserRepository>();

    [Fact]
    public async Task GetGroup_ExistingGroup_ReturnsGroupDetails()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var groupDto = new GroupDto
        {
            PublicId = groupPublicId,
            Name = "Marketing Team",
            Description = "Marketing group"
        };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(groupDto);

        var handler = new GetGroupQueryHandler(_groupRepository);
        var query = new GetGroupQuery { PublicId = groupPublicId };

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(groupPublicId, result.PublicId);
        Assert.Equal("Marketing Team", result.Name);
    }

    [Fact]
    public async Task GetGroup_GroupNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns((GroupDto?)null);

        var handler = new GetGroupQueryHandler(_groupRepository);
        var query = new GetGroupQuery { PublicId = groupPublicId };

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => handler.HandleAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task ListGroups_ReturnsAllTenantGroups()
    {
        // Arrange
        var groups = new List<GroupDto>
        {
            new() { PublicId = Guid.NewGuid(), Name = "Group A" },
            new() { PublicId = Guid.NewGuid(), Name = "Group B" }
        };

        _groupRepository.ListPagedAsync("SearchTerm", 1, 20, Arg.Any<CancellationToken>())
            .Returns((groups, 2));

        var handler = new ListGroupsQueryHandler(_groupRepository);
        var query = new ListGroupsQuery { Search = "SearchTerm", Page = 1, PageSize = 20 };

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Items);
        Assert.Equal(2, result.Total);
        Assert.Contains(result.Items, g => g.Name == "Group A");
        Assert.Contains(result.Items, g => g.Name == "Group B");
    }

    [Fact]
    public async Task ListGroupMembers_ReturnsGroupMembers()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var members = new List<GroupMemberDto>
        {
            new() { UserPublicId = Guid.NewGuid(), UserName = "User A", UserEmail = "a@a.com" },
            new() { UserPublicId = Guid.NewGuid(), UserName = "User B", UserEmail = "b@b.com" }
        };

        _groupRepository.ListMembersAsync(groupPublicId, 1, 10, Arg.Any<CancellationToken>())
            .Returns((members, 2));

        var handler = new ListGroupMembersQueryHandler(_groupRepository);
        var query = new ListGroupMembersQuery { GroupPublicId = groupPublicId, Page = 1, PageSize = 10 };

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result.Items);
        Assert.Equal(2, result.Total);
        Assert.Contains(result.Items, m => m.UserName == "User A");
    }

    [Fact]
    public async Task GetSharedApps_ReturnsAppsSharedWithGroup()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var sharedApps = new List<SharedAppDto>
        {
            new SharedAppDto { AppPublicId = Guid.NewGuid(), AppRolePublicId = Guid.NewGuid(), AppRoleName = "Role 1" },
            new SharedAppDto { AppPublicId = Guid.NewGuid(), AppRolePublicId = Guid.NewGuid(), AppRoleName = "Role 2" }
        };

        _groupRepository.GetSharedAppsAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(sharedApps);

        var handler = new GetSharedAppsQueryHandler(_groupRepository);
        var query = new GetSharedAppsQuery { GroupPublicId = groupPublicId };

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(sharedApps, result);
    }

    [Fact]
    public async Task GetUserEffectivePermissions_ReturnsCombinedPermissions()
    {
        // Arrange
        var userPublicId = Guid.NewGuid();
        var permissionsDto = new UserEffectivePermissionsDto
        {
            UserPublicId = userPublicId,
            UserName = "Test User",
            UserEmail = "test@user.com",
            Apps = new List<AppPermissionDetailDto>
            {
                new()
                {
                    AppPublicId = Guid.NewGuid(),
                    AppName = "Test App",
                    ConsolidatedPermissions = new List<string> { "Read", "Write" }
                }
            }
        };

        _appUserRepository.GetUserEffectivePermissionsAsync(userPublicId, Arg.Any<CancellationToken>())
            .Returns(permissionsDto);

        var handler = new GetUserEffectivePermissionsQueryHandler(_appUserRepository);
        var query = new GetUserEffectivePermissionsQuery { UserPublicId = userPublicId };

        // Act
        var result = await handler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(userPublicId, result.UserPublicId);
        Assert.Equal("Test User", result.UserName);
        Assert.Single(result.Apps);
        Assert.Equal("Test App", result.Apps[0].AppName);
        Assert.Contains("Read", result.Apps[0].ConsolidatedPermissions);
    }
}
