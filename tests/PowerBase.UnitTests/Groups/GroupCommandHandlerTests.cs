using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Commands.AddGroupMembers;
using PowerBase.Application.Groups.Commands.CreateGroup;
using PowerBase.Application.Groups.Commands.DeleteGroup;
using PowerBase.Application.Groups.Commands.RemoveGroupMember;
using PowerBase.Application.Groups.Commands.ShareGroupWithApp;
using PowerBase.Application.Groups.Commands.UnshareGroupFromApp;
using PowerBase.Application.Groups.Commands.UpdateGroup;
using PowerBase.Application.Groups.Common;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Groups;

public class GroupCommandHandlerTests
{
    private readonly IGroupRepository _groupRepository = Substitute.For<IGroupRepository>();
    private readonly IAppRoleRepository _appRoleRepository = Substitute.For<IAppRoleRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepository = Substitute.For<IAuditRepository>();

    public GroupCommandHandlerTests()
    {
        _queryContext.UserId.Returns(1001);
        _queryContext.TenantId.Returns(500);
    }

    [Fact]
    public async Task CreateGroup_ValidCommand_CreatesGroupAndLogsAudit()
    {
        // Arrange
        var command = new CreateGroupCommand
        {
            Name = "Marketing Team",
            Description = "Marketing department group"
        };

        _groupRepository.ExistsByNameAsync(command.Name, null, Arg.Any<CancellationToken>())
            .Returns(false);

        _groupRepository.CreateAsync(Arg.Any<Group>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var g = callInfo.Arg<Group>();
                g.Id = 10;
                return g;
            });

        var handler = new CreateGroupCommandHandler(_groupRepository, _queryContext, _auditRepository);

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Marketing Team", result.Name);
        Assert.Equal("Marketing department group", result.Description);

        await _groupRepository.Received(1).CreateAsync(Arg.Is<Group>(g => 
            g.Name == "Marketing Team" && 
            g.Description == "Marketing department group" && 
            g.CreatedBy == 1001
        ), Arg.Any<CancellationToken>());

        await _auditRepository.Received(1).LogActivityAsync(
            "Created",
            "Group",
            result.PublicId.ToString(),
            "Group created: Marketing Team",
            null,
            Arg.Is<string>(x => x == null),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task CreateGroup_DuplicateName_ThrowsDuplicateException()
    {
        // Arrange
        var command = new CreateGroupCommand { Name = "Existing Group" };

        _groupRepository.ExistsByNameAsync(command.Name, null, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new CreateGroupCommandHandler(_groupRepository, _queryContext, _auditRepository);

        // Act & Assert
        await Assert.ThrowsAsync<DuplicateException>(() => handler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateGroup_ValidCommand_UpdatesGroupAndLogsAudit()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var appRolePublicId = Guid.NewGuid();

        var existingGroupDto = new GroupDto
        {
            PublicId = groupPublicId,
            Name = "Old Name",
            Description = "Old Desc"
        };

        var command = new UpdateGroupCommand
        {
            PublicId = groupPublicId,
            Name = "New Name",
            Description = "New Desc"
        };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(existingGroupDto);

        _groupRepository.ExistsByNameAsync(command.Name, groupPublicId, Arg.Any<CancellationToken>())
            .Returns(false);

        _groupRepository.UpdateAsync(groupPublicId, command.Name, command.Description, 1001, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new UpdateGroupCommandHandler(_groupRepository, _queryContext, _auditRepository);

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        await _groupRepository.Received(1).UpdateAsync(groupPublicId, "New Name", "New Desc", 1001, Arg.Any<CancellationToken>());
        await _auditRepository.Received(1).LogActivityAsync(
            "Updated",
            "Group",
            groupPublicId.ToString(),
            "Group updated: New Name",
            null,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task UpdateGroup_GroupNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var command = new UpdateGroupCommand { PublicId = groupPublicId, Name = "Name" };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns((GroupDto?)null);

        var handler = new UpdateGroupCommandHandler(_groupRepository, _queryContext, _auditRepository);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => handler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateGroup_DuplicateName_ThrowsDuplicateException()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var command = new UpdateGroupCommand { PublicId = groupPublicId, Name = "Existing Name" };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(new GroupDto { PublicId = groupPublicId, Name = "Old Name" });

        _groupRepository.ExistsByNameAsync(command.Name, groupPublicId, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new UpdateGroupCommandHandler(_groupRepository, _queryContext, _auditRepository);

        // Act & Assert
        await Assert.ThrowsAsync<DuplicateException>(() => handler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteGroup_ExistingGroup_DeletesAndLogsAudit()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var existingGroupDto = new GroupDto { PublicId = groupPublicId, Name = "To Delete" };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(existingGroupDto);

        _groupRepository.DeleteAsync(groupPublicId, 1001, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new DeleteGroupCommandHandler(_groupRepository, _queryContext, _auditRepository);

        // Act
        var result = await handler.HandleAsync(new DeleteGroupCommand { PublicId = groupPublicId }, CancellationToken.None);

        // Assert
        Assert.True(result);
        await _groupRepository.Received(1).DeleteAsync(groupPublicId, 1001, Arg.Any<CancellationToken>());
        await _auditRepository.Received(1).LogActivityAsync(
            "Deleted",
            "Group",
            groupPublicId.ToString(),
            "Group deleted: To Delete",
            null,
            null,
            null,
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task DeleteGroup_GroupNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns((GroupDto?)null);

        var handler = new DeleteGroupCommandHandler(_groupRepository, _queryContext, _auditRepository);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => handler.HandleAsync(new DeleteGroupCommand { PublicId = groupPublicId }, CancellationToken.None));
    }

    [Fact]
    public async Task AddGroupMembers_ValidUsers_AddsMembersAndLogsAudit()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var userPublicIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var existingGroupDto = new GroupDto { PublicId = groupPublicId, Name = "Group" };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(existingGroupDto);

        _groupRepository.AddMembersAsync(groupPublicId, userPublicIds, 1001, Arg.Any<CancellationToken>())
            .Returns(2);

        var handler = new AddGroupMembersCommandHandler(_groupRepository, _queryContext, _auditRepository);

        var command = new AddGroupMembersCommand
        {
            GroupPublicId = groupPublicId,
            UserPublicIds = userPublicIds
        };

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(2, result);
        await _groupRepository.Received(1).AddMembersAsync(groupPublicId, userPublicIds, 1001, Arg.Any<CancellationToken>());
        await _auditRepository.Received(1).LogActivityAsync(
            "Updated",
            "Group",
            groupPublicId.ToString(),
            "Added 2 member(s) to group 'Group'",
            null,
            Arg.Is<string>(x => x == null),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task AddGroupMembers_GroupNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns((GroupDto?)null);

        var handler = new AddGroupMembersCommandHandler(_groupRepository, _queryContext, _auditRepository);
        var command = new AddGroupMembersCommand { GroupPublicId = groupPublicId, UserPublicIds = new List<Guid> { Guid.NewGuid() } };

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => handler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveGroupMember_ValidMember_RemovesMemberAndLogsAudit()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var userPublicId = Guid.NewGuid();
        var existingGroupDto = new GroupDto { PublicId = groupPublicId, Name = "Group" };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(existingGroupDto);

        _groupRepository.RemoveMemberAsync(groupPublicId, userPublicId, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new RemoveGroupMemberCommandHandler(_groupRepository, _auditRepository);
        var command = new RemoveGroupMemberCommand { GroupPublicId = groupPublicId, UserPublicId = userPublicId };

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        await _groupRepository.Received(1).RemoveMemberAsync(groupPublicId, userPublicId, Arg.Any<CancellationToken>());
        await _auditRepository.Received(1).LogActivityAsync(
            "Updated",
            "Group",
            groupPublicId.ToString(),
            $"Removed member {userPublicId} from group 'Group'",
            null,
            Arg.Any<string>(),
            null,
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task RemoveGroupMember_GroupNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns((GroupDto?)null);

        var handler = new RemoveGroupMemberCommandHandler(_groupRepository, _auditRepository);
        var command = new RemoveGroupMemberCommand { GroupPublicId = groupPublicId, UserPublicId = Guid.NewGuid() };

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => handler.HandleAsync(command, CancellationToken.None));
    }



    [Fact]
    public async Task ShareGroup_ValidApp_SharesGroupAndLogsAudit()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var appPublicIds = new List<Guid> { Guid.NewGuid() };
        var appRolePublicId = Guid.NewGuid();
        var existingGroupDto = new GroupDto { PublicId = groupPublicId, Name = "Group" };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(existingGroupDto);

        _groupRepository.ShareWithAppsAsync(groupPublicId, appPublicIds, 1001, appRolePublicId, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new ShareGroupWithAppCommandHandler(_groupRepository, _queryContext, _auditRepository);
        var command = new ShareGroupWithAppCommand
        {
            GroupPublicId = groupPublicId,
            AppPublicIds = appPublicIds,
            AppRolePublicId = appRolePublicId
        };

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        await _groupRepository.Received(1).ShareWithAppsAsync(groupPublicId, appPublicIds, 1001, appRolePublicId, Arg.Any<CancellationToken>());
        await _auditRepository.Received(1).LogActivityAsync(
            "Updated",
            "Group",
            groupPublicId.ToString(),
            "Shared group 'Group' with 1 app(s)",
            null,
            Arg.Is<string>(x => x == null),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task UnshareGroup_ValidApp_UnsharesGroupAndLogsAudit()
    {
        // Arrange
        var groupPublicId = Guid.NewGuid();
        var appPublicId = Guid.NewGuid();
        var existingGroupDto = new GroupDto { PublicId = groupPublicId, Name = "Group" };

        _groupRepository.GetByPublicIdAsync(groupPublicId, Arg.Any<CancellationToken>())
            .Returns(existingGroupDto);

        _groupRepository.UnshareFromAppAsync(groupPublicId, appPublicId, Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = new UnshareGroupFromAppCommandHandler(_groupRepository, _auditRepository);
        var command = new UnshareGroupFromAppCommand { GroupPublicId = groupPublicId, AppPublicId = appPublicId };

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        await _groupRepository.Received(1).UnshareFromAppAsync(groupPublicId, appPublicId, Arg.Any<CancellationToken>());
        await _auditRepository.Received(1).LogActivityAsync(
            "Updated",
            "Group",
            groupPublicId.ToString(),
            $"Unshared group 'Group' from app {appPublicId}",
            null,
            Arg.Any<string>(),
            null,
            Arg.Any<CancellationToken>()
        );
    }
}
