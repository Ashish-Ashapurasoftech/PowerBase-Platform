using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.RemoveAppUser;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Apps;

/// <summary>
/// RemoveAppUser — Multi-Role Safety Tests
///
/// FIX (ગુજરાતી):
/// Multi-role feature પછી fallback path remove:
///   BEFORE: User.PublicId → RemoveAsync → ALL roles deleted (BUG)
///   AFTER:  User.PublicId → NotFoundException → nothing deleted (SAFE)
///
/// Callers MUST pass AppUser.PublicId (row-specific) to remove a specific role.
/// </summary>
public class RemoveAppUserFallbackTests
{
    // ── Mocks ──────────────────────────────────────────────────────────────────
    private readonly IAppRepository     _appRepo     = Substitute.For<IAppRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IQueryContext      _queryContext = Substitute.For<IQueryContext>();
    private readonly IAuditRepository   _auditRepo   = Substitute.For<IAuditRepository>();

    // ── Test Data ──────────────────────────────────────────────────────────────
    private readonly long _appId        = 5L;
    private readonly Guid _appPublicId  = Guid.NewGuid();
    private readonly long _userId       = 42L;             // John's system UserId
    private readonly Guid _userPublicId = Guid.NewGuid();  // John's User.PublicId (GUID-JOHN)
    private readonly Guid _rowAPublicId = Guid.NewGuid();  // AppUser row A (Manager)
    private readonly Guid _rowBPublicId = Guid.NewGuid();  // AppUser row B (Viewer)

    public RemoveAppUserFallbackTests()
    {
        // Actor = admin (id=100), not owner
        _queryContext.UserId.Returns(100L);

        _appRepo.GetByPublicIdAsync(_appPublicId, Arg.Any<CancellationToken>())
            .Returns(new App { Id = _appId, OwnerId = 999L }); // Owner = someone else
    }

    private RemoveAppUserCommandHandler MakeSut() =>
        new(_appRepo, _appUserRepo, _queryContext, _auditRepo);

    // ══════════════════════════════════════════════════════════════════════════
    // ✅ TEST 1 — Path 1 Happy Path: AppUser.PublicId → specific row delete only
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Path1 ✅: AppUser.PublicId send → ફક્ત specific role row delete, RemoveAsync ન ચાલે")]
    public async Task Remove_ByAssignmentPublicId_DeletesOnlyThatSpecificRow()
    {
        // Arrange: GUID-ROW-A (Manager role ની specific row PublicId) send
        _appUserRepo.GetByPublicIdAsync(_rowAPublicId, Arg.Any<CancellationToken>())
            .Returns(new AppUser
            {
                Id        = 10L,
                PublicId  = _rowAPublicId,
                AppId     = _appId,
                UserId    = _userId,
                UserEmail = "john@example.com",
                AppRoleId = 1L // Manager
            });

        var sut = MakeSut();

        // Act
        await sut.HandleAsync(new RemoveAppUserCommand(_appPublicId, _rowAPublicId));

        // Assert: ✅ Specific row delete (safe path)
        await _appUserRepo.Received(1)
            .RemoveAssignmentAsync(_appId, _rowAPublicId, Arg.Any<CancellationToken>());

        // Assert: ❌ RemoveAsync (ALL roles delete) NEVER called
        await _appUserRepo.DidNotReceive()
            .RemoveAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ✅ TEST 2 — FIXED: User.PublicId send → NotFoundException (safe)
    //
    // BEFORE FIX: RemoveAsync called → ALL roles deleted (BUG)
    // AFTER FIX:  NotFoundException thrown → nothing deleted (SAFE)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Path2 ✅ FIXED: User.PublicId send → NotFoundException, RemoveAsync ક્યારેય ન ચાલે")]
    public async Task Remove_ByUserPublicId_ThrowsNotFoundException_NothingDeleted()
    {
        // Arrange: User.PublicId (GUID-JOHN) send
        // AppUser table: GUID-JOHN ≠ GUID-ROW-A or GUID-ROW-B → NULL return
        _appUserRepo.GetByPublicIdAsync(_userPublicId, Arg.Any<CancellationToken>())
            .Returns((AppUser?)null); // Not found in AppUser table

        var sut = MakeSut();

        // Act & Assert: NotFoundException throw — safe, nothing deleted
        await sut.Invoking(s =>
                s.HandleAsync(new RemoveAppUserCommand(_appPublicId, _userPublicId)))
            .Should().ThrowAsync<NotFoundException>();

        // ✅ RemoveAsync (ALL delete) NEVER called
        await _appUserRepo.DidNotReceive()
            .RemoveAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());

        // ✅ RemoveAssignmentAsync also never called
        await _appUserRepo.DidNotReceive()
            .RemoveAssignmentAsync(Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }


    // ══════════════════════════════════════════════════════════════════════════
    // 🧪 TEST 3 — ISOLATION: Multi-role user ની specific role remove
    // John: Manager (ROW-A) + Viewer (ROW-B)
    // ROW-A remove → ROW-B safe
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "MultiRole 🧪: ROW-A (Manager) remove → ROW-B (Viewer) safe, RemoveAsync ક્યારેય ન ચાલે")]
    public async Task Remove_MultiRoleUser_ByAssignmentPublicId_OtherRoleUntouched()
    {
        // Arrange: ROW-A (Manager) remove request
        _appUserRepo.GetByPublicIdAsync(_rowAPublicId, Arg.Any<CancellationToken>())
            .Returns(new AppUser
            {
                Id        = 10L,
                PublicId  = _rowAPublicId,
                AppId     = _appId,
                UserId    = _userId,
                UserEmail = "john@example.com",
                AppRoleId = 1L // Manager
            });

        var sut = MakeSut();

        // Act: ફક્ત ROW-A (Manager) remove
        await sut.HandleAsync(new RemoveAppUserCommand(_appPublicId, _rowAPublicId));

        // Assert: ✅ ROW-A deleted
        await _appUserRepo.Received(1)
            .RemoveAssignmentAsync(_appId, _rowAPublicId, Arg.Any<CancellationToken>());

        // Assert: ✅ ROW-B (Viewer) NOT touched
        await _appUserRepo.DidNotReceive()
            .RemoveAssignmentAsync(_appId, _rowBPublicId, Arg.Any<CancellationToken>());

        // Assert: ✅ RemoveAsync (ALL delete) NEVER called
        await _appUserRepo.DidNotReceive()
            .RemoveAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 🔒 TEST 4 — SAFETY: GUID exists but different app → NotFoundException
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Safety 🔒: Different app ની assignment GUID → NotFoundException, nothing deleted")]
    public async Task Remove_AssignmentFromDifferentApp_ThrowsNotFoundException()
    {
        // Arrange: GUID-ROW-X exists in App-99 (not App-5)
        // Handler checks appUser.AppId != appId → NotFoundException (no fallback)
        var otherAppGuid = Guid.NewGuid();
        _appUserRepo.GetByPublicIdAsync(otherAppGuid, Arg.Any<CancellationToken>())
            .Returns(new AppUser
            {
                Id       = 200L,
                PublicId = otherAppGuid,
                AppId    = 99L,   // ← Different App!
                UserId   = _userId,
                AppRoleId = 1L
            });

        var sut = MakeSut();

        // Act & Assert: appUser.AppId != appId → NotFoundException
        await sut.Invoking(s =>
                s.HandleAsync(new RemoveAppUserCommand(_appPublicId, otherAppGuid)))
            .Should().ThrowAsync<NotFoundException>();

        // Nothing deleted
        await _appUserRepo.DidNotReceive()
            .RemoveAssignmentAsync(Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _appUserRepo.DidNotReceive()
            .RemoveAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 🔒 TEST 5 — SAFETY: App owner remove → UnauthorizedActionException
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Safety 🔒: App owner remove try → UnauthorizedActionException")]
    public async Task Remove_AppOwner_ThrowsUnauthorizedException()
    {
        // Arrange: John IS the owner
        var ownerGuid   = Guid.NewGuid();
        var ownerUserId = 999L;

        _appRepo.GetByPublicIdAsync(_appPublicId, Arg.Any<CancellationToken>())
            .Returns(new App { Id = _appId, OwnerId = ownerUserId }); // Owner = John

        _appUserRepo.GetByPublicIdAsync(ownerGuid, Arg.Any<CancellationToken>())
            .Returns(new AppUser
            {
                Id       = 300L,
                PublicId = ownerGuid,
                AppId    = _appId,
                UserId   = ownerUserId, // ← Owner
                AppRoleId = 1L
            });

        var sut = MakeSut();

        // Act & Assert
        await sut.Invoking(s =>
                s.HandleAsync(new RemoveAppUserCommand(_appPublicId, ownerGuid)))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await _appUserRepo.DidNotReceive()
            .RemoveAssignmentAsync(Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 🔒 TEST 6 — SAFETY: Self-remove → UnauthorizedActionException
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Safety 🔒: User himself remove try → UnauthorizedActionException")]
    public async Task Remove_Self_ThrowsUnauthorizedException()
    {
        // Arrange: Actor (UserId=100) removes himself
        var selfGuid = Guid.NewGuid();
        _appUserRepo.GetByPublicIdAsync(selfGuid, Arg.Any<CancellationToken>())
            .Returns(new AppUser
            {
                Id       = 400L,
                PublicId = selfGuid,
                AppId    = _appId,
                UserId   = 100L, // ← Same as _queryContext.UserId
                AppRoleId = 1L
            });

        var sut = MakeSut();

        // Act & Assert
        await sut.Invoking(s =>
                s.HandleAsync(new RemoveAppUserCommand(_appPublicId, selfGuid)))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await _appUserRepo.DidNotReceive()
            .RemoveAssignmentAsync(Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
