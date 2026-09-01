using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Groups.Common;
using PowerBase.Application.Groups.Queries.GetMyGroups;
using Xunit;

namespace PowerBase.UnitTests.Groups;

public class GetMyGroupsQueryHandlerTests
{
    private readonly IGroupRepository _groupRepository = Substitute.For<IGroupRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private GetMyGroupsQueryHandler CreateSut() => new(_groupRepository, _queryContext);

    [Fact]
    public async Task HandleAsync_ReturnsOnlyGroupsUserBelongsTo()
    {
        // Arrange
        const long userId = 42L;
        _queryContext.UserId.Returns(userId);

        var myGroups = new List<GroupDto>
        {
            new() { PublicId = Guid.NewGuid(), Name = "Engineering", Description = "Dev Team", MemberCount = 5 },
            new() { PublicId = Guid.NewGuid(), Name = "Leadership", Description = "Lead Team", MemberCount = 2 }
        };

        _groupRepository.GetMyGroupsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(myGroups);

        var sut = CreateSut();
        var query = new GetMyGroupsQuery();

        // Act
        var result = await sut.HandleAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(myGroups);
        await _groupRepository.Received(1).GetMyGroupsAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UserNotInAnyGroups_ReturnsEmptyList()
    {
        // Arrange
        const long userId = 42L;
        _queryContext.UserId.Returns(userId);

        _groupRepository.GetMyGroupsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<GroupDto>());

        var sut = CreateSut();
        var query = new GetMyGroupsQuery();

        // Act
        var result = await sut.HandleAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ExplicitUserIdProvided_UsesExplicitUserId()
    {
        // Arrange
        const long contextUserId = 10L;
        const long explicitUserId = 88L;
        _queryContext.UserId.Returns(contextUserId);

        var groups = new List<GroupDto>
        {
            new() { PublicId = Guid.NewGuid(), Name = "Designers", MemberCount = 3 }
        };

        _groupRepository.GetMyGroupsAsync(explicitUserId, Arg.Any<CancellationToken>())
            .Returns(groups);

        var sut = CreateSut();
        var query = new GetMyGroupsQuery { UserId = explicitUserId };

        // Act
        var result = await sut.HandleAsync(query);

        // Assert
        result.Should().HaveCount(1);
        await _groupRepository.Received(1).GetMyGroupsAsync(explicitUserId, Arg.Any<CancellationToken>());
    }
}
