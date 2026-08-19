using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.UserTokens;

public class UserTokenAppAccessEnforcementTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IFormRepository _formRepo = Substitute.For<IFormRepository>();
    private readonly IFormRuleRepository _formRuleRepo = Substitute.For<IFormRuleRepository>();
    private readonly IPageRepository _pageRepo = Substitute.For<IPageRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly AppAccessService _service;

    public UserTokenAppAccessEnforcementTests()
    {
        _queryContext.UserId.Returns(1001L);
        _queryContext.TenantId.Returns(500L);

        _service = new AppAccessService(
            _appRepo,
            _tableRepo,
            _reportRepo,
            _formRepo,
            _formRuleRepo,
            _pageRepo,
            _appUserRepo,
            _queryContext);
    }

    [Fact]
    public async Task RequirePermissionByAppIdAsync_WhenTokenRestrictedAndAppNotAllowed_ThrowsUnauthorizedActionException()
    {
        // Arrange
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L, 20L });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _service.RequirePermissionByAppIdAsync(99L, PermissionCodes.AppsRead, CancellationToken.None));

        Assert.Contains("This user token does not have access to this application", ex.Message);
        await _appUserRepo.DidNotReceive().GetUserAppPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequirePermissionByAppIdAsync_WhenTokenRestrictedAndAppAllowed_ChecksUserPermissions()
    {
        // Arrange
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L, 20L });
        _appUserRepo.GetUserAppPermissionsAsync(10L, 1001L, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.AppsRead });

        // Act
        await _service.RequirePermissionByAppIdAsync(10L, PermissionCodes.AppsRead, CancellationToken.None);

        // Assert
        await _appUserRepo.Received(1).GetUserAppPermissionsAsync(10L, 1001L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequirePermissionByAppPublicIdAsync_WhenTokenRestrictedAndAppNotAllowed_ThrowsUnauthorizedActionException()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L });
        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(99L);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _service.RequirePermissionByAppPublicIdAsync(appPublicId, PermissionCodes.AppsRead, CancellationToken.None));

        Assert.Contains("This user token does not have access to this application", ex.Message);
    }

    [Fact]
    public async Task RequirePermissionByTablePublicIdAsync_WhenTokenRestrictedAndAppNotAllowed_ThrowsUnauthorizedActionException()
    {
        // Arrange
        var tablePublicId = Guid.NewGuid();
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L });
        _tableRepo.GetAppIdByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>()).Returns(99L);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _service.RequirePermissionByTablePublicIdAsync(tablePublicId, PermissionCodes.TablesRead, CancellationToken.None));

        Assert.Contains("This user token does not have access to this application", ex.Message);
    }

    [Fact]
    public async Task RequirePermissionByReportPublicIdAsync_WhenTokenRestrictedAndAppNotAllowed_ThrowsUnauthorizedActionException()
    {
        // Arrange
        var reportPublicId = Guid.NewGuid();
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L });
        _reportRepo.GetAppIdByPublicIdAsync(reportPublicId, Arg.Any<CancellationToken>()).Returns(99L);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _service.RequirePermissionByReportPublicIdAsync(reportPublicId, PermissionCodes.ReportsRead, CancellationToken.None));

        Assert.Contains("This user token does not have access to this application", ex.Message);
    }

    [Fact]
    public async Task RequirePermissionByFormPublicIdAsync_WhenTokenRestrictedAndAppNotAllowed_ThrowsUnauthorizedActionException()
    {
        // Arrange
        var formPublicId = Guid.NewGuid();
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L });
        _formRepo.GetAppIdByPublicIdAsync(formPublicId, Arg.Any<CancellationToken>()).Returns(99L);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _service.RequirePermissionByFormPublicIdAsync(formPublicId, PermissionCodes.FormsRead, CancellationToken.None));

        Assert.Contains("This user token does not have access to this application", ex.Message);
    }

    [Fact]
    public async Task RequirePermissionByPagePublicIdAsync_WhenTokenRestrictedAndAppNotAllowed_ThrowsUnauthorizedActionException()
    {
        // Arrange
        var pagePublicId = Guid.NewGuid();
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L });
        _pageRepo.GetAppIdByPublicIdAsync(pagePublicId, Arg.Any<CancellationToken>()).Returns(99L);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _service.RequirePermissionByPagePublicIdAsync(pagePublicId, PermissionCodes.PagesRead, CancellationToken.None));

        Assert.Contains("This user token does not have access to this application", ex.Message);
    }

    [Fact]
    public async Task RequireMembershipByTablePublicIdAsync_WhenTokenRestricted_ThrowsUnauthorizedActionException_EvenForSuperAdmin()
    {
        // Arrange
        var tablePublicId = Guid.NewGuid();
        _queryContext.IsSuperAdmin.Returns(true);
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L });
        _tableRepo.GetAppIdByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>()).Returns(99L);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _service.RequireMembershipByTablePublicIdAsync(tablePublicId, CancellationToken.None));

        Assert.Contains("This user token does not have access to this application", ex.Message);
    }

    [Fact]
    public async Task RequireAppRoleAsync_WhenTokenRestrictedAndAppNotAllowed_ThrowsUnauthorizedActionException()
    {
        // Arrange
        var appPublicId = Guid.NewGuid();
        _queryContext.TokenAccessAllApps.Returns(false);
        _queryContext.AllowedAppIds.Returns(new HashSet<long> { 10L });
        _appRepo.GetIdByPublicIdAsync(appPublicId, Arg.Any<CancellationToken>()).Returns(99L);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedActionException>(() =>
            _service.RequireAppRoleAsync(appPublicId, "Admin", CancellationToken.None));

        Assert.Contains("This user token does not have access to this application", ex.Message);
    }

    [Fact]
    public async Task RequirePermissionByAppIdAsync_WhenTokenAccessAllApps_DoesNotThrowRestrictionError()
    {
        // Arrange
        _queryContext.TokenAccessAllApps.Returns(true);
        _queryContext.AllowedAppIds.Returns(new HashSet<long>());
        _appUserRepo.GetUserAppPermissionsAsync(99L, 1001L, Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.AppsRead });

        // Act
        await _service.RequirePermissionByAppIdAsync(99L, PermissionCodes.AppsRead, CancellationToken.None);

        // Assert
        await _appUserRepo.Received(1).GetUserAppPermissionsAsync(99L, 1001L, Arg.Any<CancellationToken>());
    }
}
