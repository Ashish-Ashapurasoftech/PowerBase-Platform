using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Connections.Commands.CreateConnection;
using PowerBase.Application.Connections.Common;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Connections;

/// <summary>
/// Covers the rejection paths only. Every one of them must fail BEFORE anything is written,
/// so a refused connect never leaves a half-saved account row behind. The success path opens a
/// real target-tenant DI scope and belongs to the integration suite.
/// </summary>
public class CreateConnectionCommandHandlerTests
{
    private const string RawToken = "pb_ut_abcdef1234567890";
    private const long LoggedInUserId = 11;
    private const long TokenOwnerUserId = 77;

    private readonly IPipelineAccountRepository _accountRepo = Substitute.For<IPipelineAccountRepository>();
    private readonly IUserTokenRepository _userTokenRepo = Substitute.For<IUserTokenRepository>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IAuditRepository _auditRepo = Substitute.For<IAuditRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly CreateConnectionCommandHandler _handler;

    public CreateConnectionCommandHandlerTests()
    {
        _queryContext.UserId.Returns(LoggedInUserId);
        _queryContext.TenantId.Returns(1L);

        _handler = new CreateConnectionCommandHandler(
            _accountRepo, _userTokenRepo, _tenantRepo, _auditRepo, _queryContext, _scopeFactory);
    }

    private static CreateConnectionCommand Command(string subdomain = "acme", string token = RawToken)
        => new(PipelineAccountAuthModes.UserToken, subdomain, token, null);

    private static UserToken LiveToken(long tenantId) => new()
    {
        Id = 9,
        PublicId = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = TokenOwnerUserId,
        TokenName = "PowerFlows",
        IsActive = true,
        AccessAllApps = true
    };

    private async Task AssertNothingPersistedAsync()
    {
        await _accountRepo.DidNotReceive().CreateAsync(Arg.Any<PipelineAccount>(), Arg.Any<CancellationToken>());
        await _accountRepo.DidNotReceive().RefreshCredentialAsync(Arg.Any<PipelineAccount>(), Arg.Any<CancellationToken>());
        _auditRepo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_UnknownToken_ThrowsAndPersistsNothing()
    {
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UserToken?)null);

        var ex = await FluentActions.Awaiting(() => _handler.HandleAsync(Command()))
            .Should().ThrowAsync<UnauthorizedActionException>();

        ex.Which.Message.Should().Contain("not valid");
        ex.Which.Message.Should().NotContain(RawToken); // the raw token never travels in an error
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task HandleAsync_RevokedToken_ThrowsAndPersistsNothing()
    {
        var token = LiveToken(tenantId: 42);
        token.IsActive = false;
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(token);

        await FluentActions.Awaiting(() => _handler.HandleAsync(Command()))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task HandleAsync_UnknownSubdomain_ThrowsAndPersistsNothing()
    {
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LiveToken(tenantId: 42));
        _tenantRepo.GetTenantBySlugAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);

        var ex = await FluentActions.Awaiting(() => _handler.HandleAsync(Command("nosuchrealm")))
            .Should().ThrowAsync<UnauthorizedActionException>();

        ex.Which.Message.Should().Contain("nosuchrealm");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task HandleAsync_TokenBelongsToAnotherRealm_ThrowsAndPersistsNothing()
    {
        // The token is live, the realm exists — but the token was minted against a different one.
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LiveToken(tenantId: 99));
        _tenantRepo.GetTenantBySlugAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { Id = 42, Slug = "acme", Name = "Acme", Status = "Active" });

        var ex = await FluentActions.Awaiting(() => _handler.HandleAsync(Command()))
            .Should().ThrowAsync<UnauthorizedActionException>();

        ex.Which.Message.Should().Contain("does not belong to the 'acme' realm");
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task HandleAsync_CurrentUserAuthMode_ThrowsValidationAndNeverLooksTheTokenUp()
    {
        var command = new CreateConnectionCommand(
            PipelineAccountAuthModes.CurrentUser, "acme", RawToken, null);

        await FluentActions.Awaiting(() => _handler.HandleAsync(command))
            .Should().ThrowAsync<ValidationException>();

        await _userTokenRepo.DidNotReceive().GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await AssertNothingPersistedAsync();
    }

    [Fact]
    public async Task HandleAsync_LooksTheTokenUpByHashAndNeverByRawValue()
    {
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((UserToken?)null);

        await FluentActions.Awaiting(() => _handler.HandleAsync(Command()))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await _userTokenRepo.Received(1).GetByHashAsync(
            ConnectionScopeResolver.HashToken(RawToken), Arg.Any<CancellationToken>());
        await _userTokenRepo.DidNotReceive().GetByHashAsync(RawToken, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MixedCaseSubdomain_IsNormalisedBeforeTheRealmLookup()
    {
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LiveToken(tenantId: 42));
        _tenantRepo.GetTenantBySlugAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);

        await FluentActions.Awaiting(() => _handler.HandleAsync(Command("ACME")))
            .Should().ThrowAsync<UnauthorizedActionException>();

        await _tenantRepo.Received(1).GetTenantBySlugAsync("acme", Arg.Any<CancellationToken>());
    }

    private void MockTargetTenantScope(string userEmail, string userName)
    {
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        
        var queryContext = Substitute.For<IQueryContext>();
        queryContext.UserEmail.Returns(userEmail);
        queryContext.UserName.Returns(userName);
        
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = TokenOwnerUserId, Name = userName, Email = userEmail, IsActive = true });
            
        var tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetUserRoleNameInTenantAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns("Admin");
        tenantRepo.IsActiveMemberAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(true);
            
        var permissionRepo = Substitute.For<IUserPermissionRepository>();
        permissionRepo.GetPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());

        serviceProvider.GetService(typeof(IQueryContext)).Returns(queryContext);
        serviceProvider.GetService(typeof(IUserRepository)).Returns(userRepo);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        serviceProvider.GetService(typeof(IUserPermissionRepository)).Returns(permissionRepo);

        scope.ServiceProvider.Returns(serviceProvider);
        _scopeFactory.CreateScope().Returns(scope);
    }

    [Fact]
    public async Task UserTokenConnection_UsesTokenNameAsConnectionName()
    {
        var token = LiveToken(tenantId: 42);
        token.TokenName = "My Token Name";
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(token);
        _tenantRepo.GetTenantBySlugAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { Id = 42, Slug = "acme", Name = "Acme", Status = "Active" });

        MockTargetTenantScope("owner@acme.com", "Owner");

        _accountRepo.CreateAsync(Arg.Any<PipelineAccount>(), Arg.Any<CancellationToken>())
            .Returns(x => x.Arg<PipelineAccount>());

        var result = await _handler.HandleAsync(Command("acme"));

        result.Name.Should().Be("My Token Name");
        await _accountRepo.Received(1).CreateAsync(Arg.Is<PipelineAccount>(a => a.Name == "My Token Name"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UserTokenConnection_BlankTokenName_UsesExistingFallback()
    {
        var token = LiveToken(tenantId: 42);
        token.TokenName = "   ";
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(token);
        _tenantRepo.GetTenantBySlugAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { Id = 42, Slug = "acme", Name = "Acme", Status = "Active" });

        MockTargetTenantScope("owner@acme.com", "Owner");

        _accountRepo.CreateAsync(Arg.Any<PipelineAccount>(), Arg.Any<CancellationToken>())
            .Returns(x => x.Arg<PipelineAccount>());

        var result = await _handler.HandleAsync(new CreateConnectionCommand(PipelineAccountAuthModes.UserToken, "acme", RawToken, "Custom Subdomain"));

        result.Name.Should().Be("Custom Subdomain");
        await _accountRepo.Received(1).CreateAsync(Arg.Is<PipelineAccount>(a => a.Name == "Custom Subdomain"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconnect_PreservesOrRefreshesTokenNameCorrectly()
    {
        var token = LiveToken(tenantId: 42);
        token.TokenName = "Updated Token Name";
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(token);
        _tenantRepo.GetTenantBySlugAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { Id = 42, Slug = "acme", Name = "Acme", Status = "Active" });

        MockTargetTenantScope("owner@acme.com", "Owner");

        var existing = new PipelineAccount { Id = 101, PublicId = Guid.NewGuid(), Name = "Old Name" };
        _accountRepo.GetByTokenHashAsync(Arg.Any<string>(), LoggedInUserId, Arg.Any<CancellationToken>())
            .Returns(existing);

        _accountRepo.RefreshCredentialAsync(Arg.Any<PipelineAccount>(), Arg.Any<CancellationToken>())
            .Returns(x => x.Arg<PipelineAccount>());

        var result = await _handler.HandleAsync(Command("acme"));

        result.Name.Should().Be("Updated Token Name");
        await _accountRepo.Received(1).RefreshCredentialAsync(Arg.Is<PipelineAccount>(a => a.Name == "Updated Token Name"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RawTokenSecret_IsNeverReturnedInDto()
    {
        var token = LiveToken(tenantId: 42);
        token.TokenName = "Secret Test Token";
        _userTokenRepo.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(token);
        _tenantRepo.GetTenantBySlugAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { Id = 42, Slug = "acme", Name = "Acme", Status = "Active" });

        MockTargetTenantScope("owner@acme.com", "Owner");

        _accountRepo.CreateAsync(Arg.Any<PipelineAccount>(), Arg.Any<CancellationToken>())
            .Returns(x => x.Arg<PipelineAccount>());

        var result = await _handler.HandleAsync(Command("acme"));

        result.TokenPrefix.Should().Be("pb_ut_abcd…");
        // Verify no raw token or hash properties are on the DTO class.
        typeof(PipelineAccountDto).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(new[] { "Id", "TokenHash", "UserToken", "TargetTenantId", "TargetUserId" });
    }
}
