using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pages.Commands.CreatePage;
using PowerBase.Application.Pages.Commands.DeletePages;
using PowerBase.Application.Pages.Commands.DuplicatePage;
using PowerBase.Application.Pages.Commands.PublishPage;
using PowerBase.Application.Pages.Commands.RestorePageVersion;
using PowerBase.Application.Pages.Commands.UpdatePage;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pages;

public class PageHandlerTests
{
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppRoleRepository _appRoleRepo = Substitute.For<IAppRoleRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IPageRepository _pageRepo = Substitute.For<IPageRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();

    private readonly Guid _appPublicId = Guid.NewGuid();
    private const long AppId = 1;

    public PageHandlerTests()
    {
        _appRepo.GetIdByPublicIdAsync(_appPublicId, Arg.Any<CancellationToken>()).Returns(AppId);
        _queryContext.UserId.Returns(42L);
        _queryContext.IsSuperAdmin.Returns(false);
        _queryContext.Permissions.Returns(new HashSet<string>());
    }

    private static Page MakePage(long id = 10, string pageType = "Dashboard", int currentVersionNo = 1, bool isPublished = false) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        AppId = AppId,
        PageNumber = 1,
        PageType = pageType,
        Name = "Existing Page",
        Description = "desc",
        OwnerId = 42,
        Visibility = "Personal",
        Definition = "{}",
        CurrentVersionNo = currentVersionNo,
        IsPublished = isPublished,
    };

    // ── CreatePage ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePage_Dashboard_Succeeds_AndWritesNoVersionRowYet()
    {
        // Regression guard: CreatePage must NOT pre-insert a version-1 row. CurrentVersionNo
        // starts at 1 meaning "the live row IS version 1, nothing snapshotted yet" — the first
        // edit's pre-edit snapshot (also at VersionNo 1) would collide on the (PageId, VersionNo)
        // PK otherwise (reproduces the "duplicate key value is (1, 1)" bug).
        _pageRepo.CreateAsync(Arg.Any<Page>(), Arg.Any<CancellationToken>())
            .Returns((11L, Guid.NewGuid(), 3));

        var sut = new CreatePageCommandHandler(_appRepo, _appRoleRepo, _appUserRepo, _pageRepo, _queryContext, _auditRepo);
        var result = await sut.HandleAsync(new CreatePageCommand(
            _appPublicId, "Dashboard", "My Dashboard", null, "Personal", null,
            "{}", null, null, null, null, false, 0, null));

        result.PageNumber.Should().Be(3);
        result.CurrentVersionNo.Should().Be(1);

        await _pageRepo.DidNotReceive().InsertVersionAsync(Arg.Any<PageVersion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePage_CodeType_WithoutCodePermission_ThrowsUnauthorized()
    {
        var sut = new CreatePageCommandHandler(_appRepo, _appRoleRepo, _appUserRepo, _pageRepo, _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new CreatePageCommand(
                _appPublicId, "Code", "My Code Page", null, "Personal", null,
                null, "html", "<h1>hi</h1>", null, null, false, 0, null)))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await _pageRepo.DidNotReceive().CreateAsync(Arg.Any<Page>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePage_CodeType_WithCodePermission_Succeeds()
    {
        _queryContext.Permissions.Returns(new HashSet<string> { PermissionCodes.PagesCode });
        _pageRepo.CreateAsync(Arg.Any<Page>(), Arg.Any<CancellationToken>())
            .Returns((11L, Guid.NewGuid(), 1));

        var sut = new CreatePageCommandHandler(_appRepo, _appRoleRepo, _appUserRepo, _pageRepo, _queryContext, _auditRepo);
        var result = await sut.HandleAsync(new CreatePageCommand(
            _appPublicId, "Code", "My Code Page", null, "Personal", null,
            null, "html", "<h1>hi</h1>", null, null, false, 0, null));

        result.PageType.Should().Be("Code");
    }

    [Fact]
    public async Task CreatePage_SpecificRolesWithNoRoles_FailsValidation()
    {
        var sut = new CreatePageCommandHandler(_appRepo, _appRoleRepo, _appUserRepo, _pageRepo, _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new CreatePageCommand(
                _appPublicId, "Dashboard", "P", null, "SpecificRoles", null,
                "{}", null, null, null, null, false, 0, null)))
            .Should().ThrowAsync<ValidationException>();
    }

    // ── UpdatePage — the load-bearing handler ──────────────────────────────────

    [Fact]
    public async Task UpdatePage_MissingChangeNotes_ThrowsValidationException()
    {
        var page = MakePage();
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        var sut = new UpdatePageCommandHandler(_pageRepo, _appRoleRepo, _appUserRepo, _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new UpdatePageCommand(
                page.PublicId, "New Name", null, "Personal", null,
                "{}", null, null, null, null, false, 0, null, "")))
            .Should().ThrowAsync<ValidationException>();

        await _pageRepo.DidNotReceive().InsertVersionAsync(Arg.Any<PageVersion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePage_VisibilityMyRole_PinsOwnersRoleIntoAppRolePage()
    {
        // Regression guard: a page set to "MyRole" visibility must have the owner's role
        // written to AppRolePage — GetVisiblePageAsync's visibility predicate checks EXISTS
        // against that table for MyRole/SpecificRoles pages, so without this the page becomes
        // invisible to everyone (including its own owner) the moment MyRole is selected.
        var page = MakePage(currentVersionNo: 1);
        page.OwnerId = 99;
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        _appUserRepo.GetByAppAndUserAsync(page.AppId, 99, Arg.Any<CancellationToken>())
            .Returns(new AppUser { AppId = page.AppId, UserId = 99, AppRoleId = 7 });

        var sut = new UpdatePageCommandHandler(_pageRepo, _appRoleRepo, _appUserRepo, _queryContext, _auditRepo);
        await sut.HandleAsync(new UpdatePageCommand(
            page.PublicId, "Renamed", null, "MyRole", null,
            "{}", null, null, null, null, false, 0, null, "Switch to MyRole"));

        await _pageRepo.Received(1).ReplacePageRolesAsync(
            page.Id, Arg.Is<IEnumerable<long>>(roles => roles.Contains(7)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePage_FirstEditRightAfterCreation_DoesNotCollideWithVersionOne()
    {
        // Reproduces the reported bug end-to-end at the handler boundary: Create leaves the
        // page at CurrentVersionNo=1 with NO version rows (per the fix above); the very first
        // Update must be able to snapshot at VersionNo=1 without hitting the (PageId, VersionNo)
        // PK — which is exactly what threw "duplicate key value is (1, 1)" before the fix.
        var page = MakePage(currentVersionNo: 1);
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        var sut = new UpdatePageCommandHandler(_pageRepo, _appRoleRepo, _appUserRepo, _queryContext, _auditRepo);
        var result = await sut.HandleAsync(new UpdatePageCommand(
            page.PublicId, "First Edit", null, "Personal", null,
            "{}", null, null, null, null, false, 0, null, "First real edit"));

        await _pageRepo.Received(1).InsertVersionAsync(
            Arg.Is<PageVersion>(v => v.VersionNo == 1), Arg.Any<CancellationToken>());
        result.CurrentVersionNo.Should().Be(2);
    }

    [Fact]
    public async Task UpdatePage_SnapshotsPreEditState_AtCurrentVersionNo_ThenIncrements()
    {
        var page = MakePage(currentVersionNo: 4);
        var originalName = page.Name;
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        var sut = new UpdatePageCommandHandler(_pageRepo, _appRoleRepo, _appUserRepo, _queryContext, _auditRepo);
        var result = await sut.HandleAsync(new UpdatePageCommand(
            page.PublicId, "Renamed", "new desc", "Personal", null,
            "{\"x\":1}", null, null, null, null, true, 5, "pi-star", "Renamed the page"));

        // The version snapshot must capture the OLD name/version number, not the new one —
        // version N is "what the page looked like before edit N".
        await _pageRepo.Received(1).InsertVersionAsync(
            Arg.Is<PageVersion>(v => v.VersionNo == 4 && v.Name == originalName && v.ChangeNotes == "Renamed the page"),
            Arg.Any<CancellationToken>());

        result.CurrentVersionNo.Should().Be(5);
        result.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task UpdatePage_CodeType_WithoutCodePermission_ThrowsUnauthorized()
    {
        var page = MakePage(pageType: "Code");
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        var sut = new UpdatePageCommandHandler(_pageRepo, _appRoleRepo, _appUserRepo, _queryContext, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new UpdatePageCommand(
                page.PublicId, "Name", null, "Personal", null,
                null, "html", "<p>x</p>", null, null, false, 0, null, "note")))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    // ── DeletePages ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletePages_PageFromDifferentApp_ThrowsUnauthorized()
    {
        var page = MakePage();
        page.AppId = AppId + 999; // belongs to a different app
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        var sut = new DeletePagesCommandHandler(_appRepo, _pageRepo, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new DeletePagesCommand(_appPublicId, [page.PublicId])))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await _pageRepo.DidNotReceive().SoftDeleteManyAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePages_ValidPages_SoftDeletesAll()
    {
        var page1 = MakePage();
        var page2 = MakePage(id: 20);
        _pageRepo.GetByPublicIdAsync(page1.PublicId, Arg.Any<CancellationToken>()).Returns(page1);
        _pageRepo.GetByPublicIdAsync(page2.PublicId, Arg.Any<CancellationToken>()).Returns(page2);

        var sut = new DeletePagesCommandHandler(_appRepo, _pageRepo, _auditRepo);
        await sut.HandleAsync(new DeletePagesCommand(_appPublicId, [page1.PublicId, page2.PublicId]));

        await _pageRepo.Received(1).SoftDeleteManyAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
    }

    // ── DuplicatePage ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DuplicatePage_StartsUnpublished_RegardlessOfSourceVisibility()
    {
        var source = MakePage(isPublished: true);
        source.Visibility = "Shared";
        _pageRepo.GetByPublicIdAsync(source.PublicId, Arg.Any<CancellationToken>()).Returns(source);
        _pageRepo.DuplicateAsync(source.PublicId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((99L, Guid.NewGuid(), 7));

        var sut = new DuplicatePageCommandHandler(_pageRepo, _auditRepo);
        var result = await sut.HandleAsync(new DuplicatePageCommand(source.PublicId, null));

        result.Visibility.Should().Be("Personal");
        result.IsPublished.Should().BeFalse();
        result.Name.Should().Be("Existing Page (copy)");
    }

    // ── PublishPage ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishPage_SetsPublishedVersionNo_ToCurrentVersion()
    {
        var page = MakePage(currentVersionNo: 6);
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        var sut = new PublishPageCommandHandler(_pageRepo, _auditRepo);
        await sut.HandleAsync(new PublishPageCommand(page.PublicId, true));

        await _pageRepo.Received(1).SetPublishedAsync(page.PublicId, true, 6, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishPage_Unpublish_KeepsExistingPublishedVersionNo()
    {
        var page = MakePage(currentVersionNo: 6, isPublished: true);
        page.PublishedVersionNo = 6;
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        var sut = new PublishPageCommandHandler(_pageRepo, _auditRepo);
        await sut.HandleAsync(new PublishPageCommand(page.PublicId, false));

        await _pageRepo.Received(1).SetPublishedAsync(page.PublicId, false, 6, Arg.Any<CancellationToken>());
    }

    // ── RestorePageVersion ────────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreVersion_MissingChangeNotes_ThrowsValidationException()
    {
        var sut = new RestorePageVersionCommandHandler(_pageRepo, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new RestorePageVersionCommand(Guid.NewGuid(), 2, "")))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task RestoreVersion_AppliesTargetContent_AndSnapshotsCurrentFirst()
    {
        var page = MakePage(currentVersionNo: 5);
        var target = new PageVersion
        {
            PageId = page.Id, VersionNo = 2, PageType = "Dashboard",
            Name = "Old Name", Description = "old desc", Definition = "{\"old\":true}",
            ChangeNotes = "original", WasPublished = false,
        };
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        _pageRepo.GetVersionAsync(page.PublicId, 2, Arg.Any<CancellationToken>()).Returns(target);

        var sut = new RestorePageVersionCommandHandler(_pageRepo, _auditRepo);
        var result = await sut.HandleAsync(new RestorePageVersionCommand(page.PublicId, 2, "Rolling back"));

        // Current (pre-restore) state snapshotted at VersionNo 5 before mutating.
        await _pageRepo.Received(1).InsertVersionAsync(
            Arg.Is<PageVersion>(v => v.VersionNo == 5 && v.ChangeNotes == "Rolling back"),
            Arg.Any<CancellationToken>());

        result.Name.Should().Be("Old Name");
        result.Definition.Should().Be("{\"old\":true}");
        result.CurrentVersionNo.Should().Be(6);
    }

    [Fact]
    public async Task RestoreVersion_UnknownVersionNo_ThrowsNotFoundException()
    {
        var page = MakePage();
        _pageRepo.GetByPublicIdAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        _pageRepo.GetVersionAsync(page.PublicId, 99, Arg.Any<CancellationToken>()).Returns((PageVersion?)null);

        var sut = new RestorePageVersionCommandHandler(_pageRepo, _auditRepo);

        await sut.Invoking(s => s.HandleAsync(new RestorePageVersionCommand(page.PublicId, 99, "note")))
            .Should().ThrowAsync<NotFoundException>();
    }
}
