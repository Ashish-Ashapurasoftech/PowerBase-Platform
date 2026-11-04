using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Apps.Commands.CopyApp;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Apps;

public class CopyAppCommandHandlerTests
{
    private const long USER_ID = 21;
    private const long SOURCE_APP_ID = 34;
    private const long NEW_APP_ID = 99;

    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();
    private readonly IAppCopyRepository _copyRepo = Substitute.For<IAppCopyRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IAppAccessService _appAccessService = Substitute.For<IAppAccessService>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private readonly Guid _sourcePublicId = Guid.NewGuid();
    private readonly Guid _newPublicId = Guid.NewGuid();
    private readonly Guid _userPublicId = Guid.NewGuid();

    public CopyAppCommandHandlerTests()
    {
        _queryContext.UserId.Returns(USER_ID);
        _userRepo.GetByIdAsync(USER_ID, Arg.Any<CancellationToken>())
            .Returns(new User { Id = USER_ID, PublicId = _userPublicId, Name = "Nirav", Email = "nirav@example.test" });
        _copyRepo.CopySchemaAsync(Arg.Any<AppSchemaCopyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AppSchemaCopyResult(NEW_APP_ID, _newPublicId,
                [new AppCopyWarning { Code = "CODE_PAGE", Message = "Code page \"Home\" was copied as it is." }], []));
        GivenSourceApp(securityOptions: null);
        GivenRole("Administrator");
    }

    private CopyAppCommandHandler CreateSut() =>
        new(_appRepo, _copyRepo, _appUserRepo, _appAccessService, _userRepo, _auditRepo, _queryContext);

    private void GivenSourceApp(string? securityOptions, bool isEncrypted = false) =>
        _appRepo.GetByPublicIdAsync(_sourcePublicId, Arg.Any<CancellationToken>())
            .Returns(new App { Id = SOURCE_APP_ID, PublicId = _sourcePublicId, Name = "HR", SecurityOptions = securityOptions, IsEncrypted = isEncrypted });

    private void GivenRole(string? roleName) =>
        _appUserRepo.GetUserRoleNameAsync(SOURCE_APP_ID, USER_ID, Arg.Any<CancellationToken>()).Returns(roleName);

    [Fact]
    public async Task HandleAsync_AdministratorCopies_CallsProcedureAndReturnsNewApp()
    {
        var result = await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", "Fresh department app"));

        result.PublicId.Should().Be(_newPublicId);
        result.Name.Should().Be("Copy of HR");
        result.Description.Should().Be("Fresh department app");
        result.Warnings.Should().ContainSingle(w => w.Code == "CODE_PAGE");
        await _copyRepo.Received(1).CopySchemaAsync(
            Arg.Is<AppSchemaCopyRequest>(r =>
                r.SourceAppId == SOURCE_APP_ID && r.NewName == "Copy of HR" && r.UserId == USER_ID &&
                r.UserPublicId == _userPublicId && r.UserName == "Nirav" && r.UserEmail == "nirav@example.test" &&
                !r.CopyUsers && !r.IsEncrypted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AdministratorCopies_LogsActivityInBothApps()
    {
        await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null));

        await _auditRepo.Received(1).LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.App, _newPublicId.ToString(),
            "Application copied from HR: Copy of HR", NEW_APP_ID, null, null, Arg.Any<CancellationToken>());
        await _auditRepo.Received(1).LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.App, _newPublicId.ToString(),
            "Application copied to Copy of HR", SOURCE_APP_ID, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CallerGoneAfterCopy_StillLogsActivity()
    {
        // The request is cancelled (tab closed) once the copy has committed.
        using var request = new CancellationTokenSource();
        _copyRepo.CopySchemaAsync(Arg.Any<AppSchemaCopyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                request.Cancel();
                return new AppSchemaCopyResult(NEW_APP_ID, _newPublicId, [], []);
            });

        await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null), request.Token);

        await _auditRepo.Received(2).LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.App, _newPublicId.ToString(), Arg.Any<string>(), Arg.Any<long?>(),
            null, null, Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task HandleAsync_CopyUsersAndEncryptedApp_PassesBothToProcedure()
    {
        GivenSourceApp(securityOptions: null, isEncrypted: true);

        var result = await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, CopyUsers: true));

        result.IsEncrypted.Should().BeTrue();
        await _copyRepo.Received(1).CopySchemaAsync(
            Arg.Is<AppSchemaCopyRequest>(r => r.CopyUsers && r.IsEncrypted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithProgressId_PassesItToTheCopy()
    {
        var progressId = Guid.NewGuid();

        await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, ProgressId: progressId));

        await _copyRepo.Received(1).CopySchemaAsync(
            Arg.Is<AppSchemaCopyRequest>(r => r.ProgressId == progressId && r.UserId == USER_ID), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithoutProgressId_PassesNone()
    {
        await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null));

        await _copyRepo.Received(1).CopySchemaAsync(
            Arg.Is<AppSchemaCopyRequest>(r => r.ProgressId == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NameAndDescriptionWithSpaces_AreTrimmedAndBlankDescriptionIsNull()
    {
        var result = await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "  Copy of HR  ", "   "));

        result.Name.Should().Be("Copy of HR");
        result.Description.Should().BeNull();
        await _appRepo.Received(1).NameExistsAsync("Copy of HR", Arg.Any<CancellationToken>());
        await _copyRepo.Received(1).CopySchemaAsync(
            Arg.Is<AppSchemaCopyRequest>(r => r.NewName == "Copy of HR" && r.NewDescription == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NonAdministratorAndCopyNotAllowed_ThrowsUnauthorizedAndCopiesNothing()
    {
        GivenRole("Participant");
        GivenSourceApp("{\"AllowNonAdminsToCopy\":false,\"RequireAppTokens\":true}");

        await CreateSut().Invoking(s => s.HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null)))
            .Should().ThrowAsync<UnauthorizedActionException>();
        await _copyRepo.DidNotReceive().CopySchemaAsync(Arg.Any<AppSchemaCopyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NonAdministratorAndNoSecurityOptions_ThrowsUnauthorized()
    {
        GivenRole("Viewer");

        await CreateSut().Invoking(s => s.HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null)))
            .Should().ThrowAsync<UnauthorizedActionException>();
    }

    [Fact]
    public async Task HandleAsync_NonAdministratorAndAppAllowsCopy_Copies()
    {
        GivenRole("Participant");
        GivenSourceApp("{\"allowNonAdminsToCopy\":true}");

        var result = await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null));

        result.PublicId.Should().Be(_newPublicId);
    }

    [Fact]
    public async Task HandleAsync_SuperAdmin_SkipsTheRoleCheck()
    {
        _queryContext.IsSuperAdmin.Returns(true);
        GivenRole(null);

        var result = await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null));

        result.PublicId.Should().Be(_newPublicId);
        await _appUserRepo.DidNotReceive().GetUserRoleNameAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NotAMemberOfTheApp_ThrowsAndCopiesNothing()
    {
        _appAccessService.RequireMembershipByAppPublicIdAsync(_sourcePublicId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedActionException("You are not a member of this app."));

        await CreateSut().Invoking(s => s.HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null)))
            .Should().ThrowAsync<UnauthorizedActionException>();
        await _copyRepo.DidNotReceive().CopySchemaAsync(Arg.Any<AppSchemaCopyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NameAlreadyTaken_ThrowsDuplicateAndCopiesNothing()
    {
        _appRepo.NameExistsAsync("HR", Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut().Invoking(s => s.HandleAsync(new CopyAppCommand(_sourcePublicId, "HR", null)))
            .Should().ThrowAsync<DuplicateException>();
        await _copyRepo.DidNotReceive().CopySchemaAsync(Arg.Any<AppSchemaCopyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ThrowsValidationBeforeAnyLookup()
    {
        await CreateSut().Invoking(s => s.HandleAsync(new CopyAppCommand(_sourcePublicId, "  ", null)))
            .Should().ThrowAsync<ValidationException>();
        await _appRepo.DidNotReceive().GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
