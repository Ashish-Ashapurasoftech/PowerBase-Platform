using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using PowerBase.Application.Apps.Queries.ListAppUsers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Apps;

public class ListAppUsersQueryHandlerTests
{
    private readonly IAppRepository _appRepository = Substitute.For<IAppRepository>();
    private readonly IAppUserRepository _appUserRepository = Substitute.For<IAppUserRepository>();

    private readonly ListAppUsersQueryHandler _listAppUsersHandler;
    private readonly ListAppUsersForPickerQueryHandler _listAppUsersForPickerHandler;

    public ListAppUsersQueryHandlerTests()
    {
        _listAppUsersHandler = new ListAppUsersQueryHandler(_appRepository, _appUserRepository);
        _listAppUsersForPickerHandler = new ListAppUsersForPickerQueryHandler(_appRepository, _appUserRepository);
    }

    [Fact]
    public async Task Handle_PagedRequest_ReturnsPagedAppUsersAndTotalCount()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var app = new App { Id = 45, PublicId = appPublicId, Name = "Test App" };
        var query = new ListAppUsersQuery(
            AppPublicId: appPublicId,
            Page: 1,
            PageSize: 20,
            Search: "John",
            SortBy: "userName",
            SortDesc: false,
            Role: "Admin"
        );

        var users = new List<AppUserDetail>
        {
            new AppUserDetail(Guid.NewGuid(), Guid.NewGuid(), "John Doe", "john@test.com", Guid.NewGuid(), "Admin", "Active", true, DateTime.UtcNow, false, false)
        };

        _appRepository.GetByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>())
            .Returns(app);

        _appUserRepository.ListByAppPagedAsync(45, 1, 20, "John", "Admin", "userName", false, Arg.Any<CancellationToken>())
            .Returns(users);

        _appUserRepository.CountByAppAsync(45, "John", "Admin", Arg.Any<CancellationToken>())
            .Returns(1);

        // Act
        var result = await _listAppUsersHandler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Single(result.Items);
        Assert.Equal("John Doe", result.Items[0].UserName);
        Assert.Equal("john@test.com", result.Items[0].UserEmail);
    }

    [Fact]
    public async Task Handle_ExportRequest_ReturnsAllFilteredUsers()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var app = new App { Id = 45, PublicId = appPublicId, Name = "Test App" };
        var query = new ListAppUsersQuery(
            AppPublicId: appPublicId,
            Page: 1,
            PageSize: 20,
            Search: "John",
            SortBy: "userName",
            SortDesc: false,
            Role: "Admin",
            IsExport: true
        );

        var users = new List<AppUserDetail>
        {
            new AppUserDetail(Guid.NewGuid(), Guid.NewGuid(), "John Doe", "john@test.com", Guid.NewGuid(), "Admin", "Active", true, DateTime.UtcNow, false, false),
            new AppUserDetail(Guid.NewGuid(), Guid.NewGuid(), "Johnny Boy", "johnny@test.com", Guid.NewGuid(), "Admin", "Active", true, DateTime.UtcNow, false, false)
        };

        _appRepository.GetByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>())
            .Returns(app);

        _appUserRepository.ListByAppFilteredAsync(45, "John", "Admin", "userName", false, Arg.Any<CancellationToken>())
            .Returns(users);

        // Act
        var result = await _listAppUsersHandler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Items.Count);
        await _appUserRepository.DidNotReceive().ListByAppPagedAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), 
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidPageAndPageSize_EnforcesDefaults()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var app = new App { Id = 45, PublicId = appPublicId, Name = "Test App" };
        var query = new ListAppUsersQuery(
            AppPublicId: appPublicId,
            Page: 0, // Invalid page
            PageSize: 150, // Exceeds Max Page Size
            Search: null,
            SortBy: "userName",
            SortDesc: false,
            Role: null
        );

        _appRepository.GetByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>())
            .Returns(app);

        _appUserRepository.ListByAppPagedAsync(45, 1, 20, null, null, "userName", false, Arg.Any<CancellationToken>())
            .Returns(new List<AppUserDetail>());

        // Act
        var result = await _listAppUsersHandler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
        await _appUserRepository.Received(1).ListByAppPagedAsync(45, 1, 20, null, null, "userName", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidSortField_EnforcesDefaultSort()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var app = new App { Id = 45, PublicId = appPublicId, Name = "Test App" };
        var query = new ListAppUsersQuery(
            AppPublicId: appPublicId,
            Page: 1,
            PageSize: 20,
            Search: null,
            SortBy: "invalidField", // Unsupported sort field
            SortDesc: false,
            Role: null
        );

        _appRepository.GetByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>())
            .Returns(app);

        _appUserRepository.ListByAppPagedAsync(45, 1, 20, null, null, "userName", false, Arg.Any<CancellationToken>())
            .Returns(new List<AppUserDetail>());

        // Act
        await _listAppUsersHandler.HandleAsync(query, CancellationToken.None);

        // Assert
        await _appUserRepository.Received(1).ListByAppPagedAsync(45, 1, 20, null, null, "userName", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PickerRequest_ReturnsPickerAppUsers()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        var query = new ListAppUsersForPickerQuery(appPublicId);

        var users = new List<AppUserDetail>
        {
            new AppUserDetail(Guid.NewGuid(), Guid.NewGuid(), "Jane Doe", "jane@test.com", Guid.NewGuid(), "Member", "Active", true, DateTime.UtcNow, false, false)
        };

        _appRepository.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>())
            .Returns(45);

        _appUserRepository.ListForUserPickerAsync(45, Arg.Any<CancellationToken>())
            .Returns(users);

        // Act
        var result = await _listAppUsersForPickerHandler.HandleAsync(query, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Jane Doe", result[0].UserName);
        Assert.Equal("jane@test.com", result[0].UserEmail);
        Assert.True(result[0].ShowInUserPickers);
    }
}
