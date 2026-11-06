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
    private readonly IMessagePublisher _messagePublisher = Substitute.For<IMessagePublisher>();
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
        new(_appRepo, _copyRepo, _appUserRepo, _appAccessService, _userRepo, _auditRepo, _messagePublisher, _queryContext);

    private void GivenDataCopy(params long[] tablesWithRecords) =>
        _copyRepo.CopyWithDataAsync(Arg.Any<AppDataCopyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AppDataCopyResult(NEW_APP_ID, _newPublicId,
                [new AppCopyWarning { Code = "CODE_PAGE", Message = "Code page \"Home\" was copied as it is." }], 1506503, tablesWithRecords));

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
    public async Task HandleAsync_WithData_CopiesRecordsInsteadOfStructureOnly()
    {
        GivenDataCopy(201, 202);

        var result = await CreateSut().HandleAsync(new CopyAppCommand(
            _sourcePublicId, "Copy of HR", null, CopyUsers: true, WithData: true, IncludeFiles: false));

        result.PublicId.Should().Be(_newPublicId);
        result.RecordsCopied.Should().Be(1506503);
        result.Warnings.Should().ContainSingle(w => w.Code == "CODE_PAGE");
        await _copyRepo.Received(1).CopyWithDataAsync(
            Arg.Is<AppDataCopyRequest>(r => !r.IncludeFiles && r.Schema.CopyUsers && r.Schema.SourceAppId == SOURCE_APP_ID && r.Schema.NewName == "Copy of HR"),
            Arg.Any<CancellationToken>());
        await _copyRepo.DidNotReceive().CopySchemaAsync(Arg.Any<AppSchemaCopyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithData_CopyDoesNotStopWithTheRequest()
    {
        // The dialog may be closed and the tab refreshed while the copy runs: its first step must not be rolled back then.
        GivenDataCopy();
        using var request = new CancellationTokenSource();

        await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, WithData: true), request.Token);

        await _copyRepo.Received(1).CopyWithDataAsync(Arg.Any<AppDataCopyRequest>(), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task HandleAsync_WithData_RemovesStaleCopiesFirst()
    {
        GivenDataCopy();

        await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, WithData: true));

        Received.InOrder(() =>
        {
            _copyRepo.DiscardStaleDataCopiesAsync(Arg.Any<CancellationToken>());
            _copyRepo.CopyWithDataAsync(Arg.Any<AppDataCopyRequest>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task HandleAsync_WithData_StaleCleanUpFails_StillCopies()
    {
        GivenDataCopy();
        _copyRepo.DiscardStaleDataCopiesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("busy"));

        var result = await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, WithData: true));

        result.RecordsCopied.Should().Be(1506503);
    }

    [Fact]
    public async Task HandleAsync_WithData_QueuesASearchReindexPerTableWithRecords()
    {
        GivenDataCopy(201, 202);
        _queryContext.TenantId.Returns(44);
        var published = new List<SearchIndexMessage>();
        await _messagePublisher.PublishBatchAsync(Arg.Do<IEnumerable<SearchIndexMessage>>(m => published.AddRange(m)), Arg.Any<CancellationToken>());

        await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, WithData: true));

        published.Should().HaveCount(2);
        published.Should().OnlyContain(m => m.Action == IndexAction.ReindexTable && m.TenantId == 44 && m.AppId == NEW_APP_ID && m.AfterId == 0);
        published.Select(m => m.TableId).Should().BeEquivalentTo(new long[] { 201, 202 });
    }

    [Fact]
    public async Task HandleAsync_WithData_SearchQueueFails_TheCopyStillSucceeds()
    {
        GivenDataCopy(201);
        _messagePublisher.PublishBatchAsync(Arg.Any<IEnumerable<SearchIndexMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Service Bus down"));

        var result = await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, WithData: true));

        result.PublicId.Should().Be(_newPublicId);
    }

    [Fact]
    public async Task HandleAsync_WithData_LogsTheRecordCount()
    {
        GivenDataCopy();

        await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, WithData: true));

        await _auditRepo.Received(1).LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.App, _newPublicId.ToString(),
            "Application copied from HR with 1506503 records: Copy of HR", NEW_APP_ID, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithDataByNonAdministratorAllowedToCopy_ThrowsAndCopiesNothing()
    {
        GivenRole("Participant");
        GivenSourceApp("{\"allowNonAdminsToCopy\":true}");

        await CreateSut().Invoking(s => s.HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, WithData: true)))
            .Should().ThrowAsync<UnauthorizedActionException>().WithMessage("*administrators*data*");
        await _copyRepo.DidNotReceive().CopyWithDataAsync(Arg.Any<AppDataCopyRequest>(), Arg.Any<CancellationToken>());
        await _copyRepo.DidNotReceive().CopySchemaAsync(Arg.Any<AppSchemaCopyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithDataBySuperAdmin_Copies()
    {
        _queryContext.IsSuperAdmin.Returns(true);
        GivenRole(null);
        GivenDataCopy();

        var result = await CreateSut().HandleAsync(new CopyAppCommand(_sourcePublicId, "Copy of HR", null, WithData: true));

        result.RecordsCopied.Should().Be(1506503);
    }

    [Fact]
    public async Task HandleAsync_EmptyName_ThrowsValidationBeforeAnyLookup()
    {
        await CreateSut().Invoking(s => s.HandleAsync(new CopyAppCommand(_sourcePublicId, "  ", null)))
            .Should().ThrowAsync<ValidationException>();
        await _appRepo.DidNotReceive().GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
