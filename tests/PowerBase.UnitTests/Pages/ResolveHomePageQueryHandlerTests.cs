using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pages.Queries.ResolveHomePage;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pages;

public class ResolveHomePageQueryHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IPageRepository _pageRepo = Substitute.For<IPageRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly Guid _appPublicId = Guid.NewGuid();
    private const long AppId = 1;

    private readonly Page _appDefault = new() { Id = 10, PublicId = Guid.NewGuid(), PageType = PageTypes.Dashboard, IsDefaultHome = true };
    private readonly Page _salesHome = new() { Id = 11, PublicId = Guid.NewGuid(), PageType = PageTypes.Dashboard };
    private readonly Page _opsHome = new() { Id = 12, PublicId = Guid.NewGuid(), PageType = PageTypes.Dashboard };

    public ResolveHomePageQueryHandlerTests()
    {
        _appRepo.GetIdByPublicIdAsync(_appPublicId, Arg.Any<CancellationToken>()).Returns(AppId);
        _queryContext.UserId.Returns(42L);
        ShowPages(_appDefault, _salesHome, _opsHome);
    }

    private void ShowPages(params Page[] visible) =>
        _pageRepo.ListVisibleByAppAsync(AppId, null, Arg.Any<CancellationToken>()).Returns(visible.ToList());

    private void UserHasRoles(params long[] roleIds) =>
        _appUserRepo.GetUserAppRoleIdsAsync(AppId, 42L, Arg.Any<CancellationToken>()).Returns(roleIds.ToList());

    private void AppHasRoles(params AppRoleDetail[] roles) =>
        _appRoleRepo.ListDetailsByAppIdAsync(AppId, Arg.Any<CancellationToken>()).Returns(roles.ToList());

    private static AppRoleDetail Role(long id, int? rank, Guid? homePage) =>
        new(id, Guid.NewGuid(), AppId, $"Role{id}", false, false, [], "None", rank, [], homePage);

    private Task<ResolvedHomePage> ResolveAsync() =>
        new ResolveHomePageQueryHandler(_appRepo, _appUserRepo, _appRoleRepo, _pageRepo, _queryContext)
            .HandleAsync(new ResolveHomePageQuery(_appPublicId));

    [Fact]
    public async Task Role_with_home_page_wins_over_app_default()
    {
        UserHasRoles(1);
        AppHasRoles(Role(1, 2, _salesHome.PublicId));

        var result = await ResolveAsync();

        result.PagePublicId.Should().Be(_salesHome.PublicId);
        result.Source.Should().Be("Role");
    }

    [Fact]
    public async Task Best_ranked_role_with_a_home_page_wins_for_multi_role_users()
    {
        UserHasRoles(1, 2, 3);
        AppHasRoles(Role(1, 5, _salesHome.PublicId), Role(2, 2, _opsHome.PublicId), Role(3, 1, null));

        (await ResolveAsync()).PagePublicId.Should().Be(_opsHome.PublicId);
    }

    [Fact]
    public async Task Roles_the_user_does_not_hold_are_ignored()
    {
        UserHasRoles(1);
        AppHasRoles(Role(1, null, null), Role(2, 1, _opsHome.PublicId));

        var result = await ResolveAsync();

        result.PagePublicId.Should().Be(_appDefault.PublicId);
        result.Source.Should().Be("App");
    }

    [Fact]
    public async Task Hidden_role_home_page_falls_through_to_next_role_then_app_default()
    {
        UserHasRoles(1, 2);
        AppHasRoles(Role(1, 1, _opsHome.PublicId), Role(2, 2, _salesHome.PublicId));
        ShowPages(_appDefault, _salesHome);

        (await ResolveAsync()).PagePublicId.Should().Be(_salesHome.PublicId);

        ShowPages(_appDefault);
        (await ResolveAsync()).PagePublicId.Should().Be(_appDefault.PublicId);
    }

    [Fact]
    public async Task No_role_home_and_no_app_default_resolves_to_none()
    {
        UserHasRoles(1);
        AppHasRoles(Role(1, 1, null));
        ShowPages(_salesHome);

        var result = await ResolveAsync();

        result.PagePublicId.Should().BeNull();
        result.Source.Should().Be("None");
    }
}
