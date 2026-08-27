using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Connections.Common;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.Connections;

public class ConnectionScopeResolverTests
{
    private const long LoggedInUserId = 11;   // L — owns the account row
    private const long TokenOwnerUserId = 77; // T — the token's user
    private const long TargetTenantId = 42;   // X — the realm the account points at

    private readonly IPipelineAccountRepository _accountRepo = Substitute.For<IPipelineAccountRepository>();
    private readonly IUserTokenRepository _userTokenRepo = Substitute.For<IUserTokenRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly ConnectionScopeResolver _resolver;

    public ConnectionScopeResolverTests()
    {
        _queryContext.UserId.Returns(LoggedInUserId);
        _resolver = new ConnectionScopeResolver(_accountRepo, _userTokenRepo, _queryContext);
    }

    private static PipelineAccount Account(Guid publicId) => new()
    {
        Id = 5,
        PublicId = publicId,
        TenantId = 1,
        CreatedByUserId = LoggedInUserId,
        Name = "owner@acme.test (acme)",
        AuthMode = PipelineAccountAuthModes.UserToken,
        Subdomain = "acme",
        TargetTenantId = TargetTenantId,
        TargetUserId = TokenOwnerUserId,
        TokenHash = ConnectionScopeResolver.HashToken("pb_ut_abcdef1234567890"),
        TokenPrefix = "pb_ut_abcd…",
        Status = PipelineAccountStatuses.Active,
        IsActive = true,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static UserToken LiveToken(bool accessAllApps = true) => new()
    {
        Id = 9,
        PublicId = Guid.NewGuid(),
        TenantId = TargetTenantId,
        UserId = TokenOwnerUserId,
        TokenName = "PowerFlows",
        IsActive = true,
        AccessAllApps = accessAllApps,
        IsDeleted = false
    };

    // ─── Hashing / masking ───────────────────────────────────────────────────────

    [Fact]
    public void HashToken_SameInput_ReturnsSameLowercaseHex()
    {
        var hash = ConnectionScopeResolver.HashToken("pb_ut_abcdef1234567890");

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
        hash.Should().Be(ConnectionScopeResolver.HashToken("pb_ut_abcdef1234567890"));
    }

    [Fact]
    public void HashToken_DifferentInput_ReturnsDifferentHash()
    {
        ConnectionScopeResolver.HashToken("pb_ut_aaaaaaaaaaaaaaaa")
            .Should().NotBe(ConnectionScopeResolver.HashToken("pb_ut_bbbbbbbbbbbbbbbb"));
    }

    [Fact]
    public void BuildTokenPrefix_LongToken_KeepsTenCharactersAndMasksTheRest()
    {
        const string raw = "pb_ut_abcdefghijklmnopqrstuvwxyz";

        var prefix = ConnectionScopeResolver.BuildTokenPrefix(raw);

        prefix.Should().Be("pb_ut_abcd…");
        raw.Should().NotBe(prefix);
        prefix.Should().NotContain("mnopqrstuvwxyz");
    }

    [Fact]
    public void BuildTokenPrefix_ShortToken_RevealsNothing()
    {
        var prefix = ConnectionScopeResolver.BuildTokenPrefix("pb_ut_abc");

        prefix.Should().Be("•••••••••");
        prefix.Should().NotContain("pb_ut");
    }

    [Fact]
    public void BuildTokenPrefix_EmptyToken_ReturnsEmpty()
        => ConnectionScopeResolver.BuildTokenPrefix(string.Empty).Should().BeEmpty();

    // ─── Gate 1: ownership ───────────────────────────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_NotASavedAccount_ReturnsNullSoCallersKeepTheirTenantPath()
    {
        var unknown = Guid.NewGuid();
        _accountRepo.GetByPublicIdForUserAsync(unknown, LoggedInUserId, Arg.Any<CancellationToken>())
            .Returns((PipelineAccount?)null);

        var scope = await _resolver.TryResolveAsync(unknown);

        scope.Should().BeNull();
        await _userTokenRepo.DidNotReceive().GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryResolveForUserAsync_UsesTheSuppliedActingUserNotTheRequestUser()
    {
        const long executionAuthority = 999;
        var publicId = Guid.NewGuid();
        var account = Account(publicId);
        _accountRepo.GetByPublicIdForUserAsync(publicId, executionAuthority, Arg.Any<CancellationToken>())
            .Returns(account);
        _userTokenRepo.GetByHashAsync(account.TokenHash!, Arg.Any<CancellationToken>()).Returns(LiveToken());

        var scope = await _resolver.TryResolveForUserAsync(publicId, executionAuthority);

        scope.Should().NotBeNull();
        await _accountRepo.Received(1).GetByPublicIdForUserAsync(publicId, executionAuthority, Arg.Any<CancellationToken>());
        await _accountRepo.DidNotReceive().GetByPublicIdForUserAsync(publicId, LoggedInUserId, Arg.Any<CancellationToken>());
    }

    // ─── Gate 2: credential still honourable ─────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_RevokedToken_MarksAccountRevokedAndThrows()
    {
        var account = Account(Guid.NewGuid());
        _userTokenRepo.GetByHashAsync(account.TokenHash!, Arg.Any<CancellationToken>())
            .Returns((UserToken?)null);

        var act = () => _resolver.ResolveAsync(account);

        (await act.Should().ThrowAsync<UnauthorizedActionException>())
            .Which.Message.Should().Contain("revoked").And.Contain("Reconnect");
        await _accountRepo.Received(1).UpdateStatusAsync(
            account.Id, PipelineAccountStatuses.Revoked, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_TokenDeactivated_MarksAccountRevokedAndThrows()
    {
        var account = Account(Guid.NewGuid());
        var token = LiveToken();
        token.IsActive = false;
        _userTokenRepo.GetByHashAsync(account.TokenHash!, Arg.Any<CancellationToken>()).Returns(token);

        await FluentActions.Awaiting(() => _resolver.ResolveAsync(account))
            .Should().ThrowAsync<UnauthorizedActionException>();
        await _accountRepo.Received(1).UpdateStatusAsync(
            account.Id, PipelineAccountStatuses.Revoked, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_TokenMovedToAnotherRealm_MarksAccountUnavailableAndThrows()
    {
        var account = Account(Guid.NewGuid());
        var token = LiveToken();
        token.TenantId = TargetTenantId + 1; // token re-minted elsewhere
        _userTokenRepo.GetByHashAsync(account.TokenHash!, Arg.Any<CancellationToken>()).Returns(token);

        (await FluentActions.Awaiting(() => _resolver.ResolveAsync(account))
            .Should().ThrowAsync<UnauthorizedActionException>())
            .Which.Message.Should().Contain("no longer matches the realm");
        await _accountRepo.Received(1).UpdateStatusAsync(
            account.Id, PipelineAccountStatuses.Unavailable, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_TokenBelongsToAnotherUser_MarksAccountUnavailableAndThrows()
    {
        var account = Account(Guid.NewGuid());
        var token = LiveToken();
        token.UserId = TokenOwnerUserId + 1;
        _userTokenRepo.GetByHashAsync(account.TokenHash!, Arg.Any<CancellationToken>()).Returns(token);

        await FluentActions.Awaiting(() => _resolver.ResolveAsync(account))
            .Should().ThrowAsync<UnauthorizedActionException>();
        await _accountRepo.Received(1).UpdateStatusAsync(
            account.Id, PipelineAccountStatuses.Unavailable, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_InactiveAccount_ThrowsWithoutTouchingTheTokenStore()
    {
        var account = Account(Guid.NewGuid());
        account.IsActive = false;
        account.Status = PipelineAccountStatuses.Revoked;

        await FluentActions.Awaiting(() => _resolver.ResolveAsync(account))
            .Should().ThrowAsync<UnauthorizedActionException>();
        await _userTokenRepo.DidNotReceive().GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_AccountWithoutStoredCredential_Throws()
    {
        var account = Account(Guid.NewGuid());
        account.TokenHash = null;

        await FluentActions.Awaiting(() => _resolver.ResolveAsync(account))
            .Should().ThrowAsync<UnauthorizedActionException>();
        await _userTokenRepo.DidNotReceive().GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_LiveUnrestrictedToken_ReturnsScopeForTheTokenOwnerAndRealm()
    {
        var account = Account(Guid.NewGuid());
        _userTokenRepo.GetByHashAsync(account.TokenHash!, Arg.Any<CancellationToken>()).Returns(LiveToken());

        var scope = await _resolver.ResolveAsync(account);

        scope.AccountId.Should().Be(account.Id);
        scope.ConnectionPublicId.Should().Be(account.PublicId);
        scope.TargetTenantId.Should().Be(TargetTenantId);
        scope.TargetUserId.Should().Be(TokenOwnerUserId);
        scope.IsUserToken.Should().BeTrue();
        scope.TokenAccessAllApps.Should().BeTrue();
        scope.AllowedAppIds.Should().BeEmpty();
        await _userTokenRepo.DidNotReceive().GetAllowedAppIdsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_RestrictedToken_CarriesTheAllowedAppIdsIntoTheScope()
    {
        var account = Account(Guid.NewGuid());
        var token = LiveToken(accessAllApps: false);
        _userTokenRepo.GetByHashAsync(account.TokenHash!, Arg.Any<CancellationToken>()).Returns(token);
        _userTokenRepo.GetAllowedAppIdsAsync(token.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<long>)new HashSet<long> { 3, 8 });

        var scope = await _resolver.ResolveAsync(account);

        scope.TokenAccessAllApps.Should().BeFalse();
        scope.AllowedAppIds.Should().BeEquivalentTo(new[] { 3L, 8L });
    }

    [Fact]
    public async Task ResolveAsync_Success_TouchesLastUsedOnBothRows()
    {
        var account = Account(Guid.NewGuid());
        var token = LiveToken();
        _userTokenRepo.GetByHashAsync(account.TokenHash!, Arg.Any<CancellationToken>()).Returns(token);

        await _resolver.ResolveAsync(account);

        await _accountRepo.Received(1).UpdateLastUsedAtAsync(account.Id, Arg.Any<CancellationToken>());
        await _userTokenRepo.Received(1).UpdateLastUsedAtAsync(token.Id, Arg.Any<CancellationToken>());
    }

    // ─── DTO must stay display-safe ──────────────────────────────────────────────

    [Fact]
    public void ToDto_CopiesDisplayFieldsOnly_NeverTheHashOrInternalId()
    {
        var account = Account(Guid.NewGuid());
        account.LastUsedAt = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        var dto = ConnectionScopeResolver.ToDto(account);

        dto.PublicId.Should().Be(account.PublicId);
        dto.Name.Should().Be(account.Name);
        dto.AuthMode.Should().Be(PipelineAccountAuthModes.UserToken);
        dto.Subdomain.Should().Be("acme");
        dto.TokenPrefix.Should().Be("pb_ut_abcd…");
        dto.Status.Should().Be(PipelineAccountStatuses.Active);
        dto.CreatedAt.Should().Be(account.CreatedAt);
        dto.LastUsedAt.Should().Be(account.LastUsedAt);

        var json = JsonSerializer.Serialize(dto);
        json.Should().NotContain(account.TokenHash!);
        json.Should().NotContain("\"Id\"");

        typeof(PipelineAccountDto).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(new[] { "Id", "TokenHash", "UserToken", "TargetTenantId", "TargetUserId" });
    }

    [Fact]
    public void ToDto_DeactivatedRow_ReportsUnavailableSoTheDropdownCanFlagIt()
    {
        var account = Account(Guid.NewGuid());
        account.IsActive = false;

        ConnectionScopeResolver.ToDto(account).Status
            .Should().Be(PipelineAccountStatuses.Unavailable);
    }
}
