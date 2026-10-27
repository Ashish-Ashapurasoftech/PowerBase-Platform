using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Access;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.ConnectedTables;

public class ConnectedAccessVerifierTests
{
    private const long Consumer = 1;
    private const long Provider = 2;

    private readonly IConnectedControlRepository _control = Substitute.For<IConnectedControlRepository>();
    private readonly IConnectedConnectionRepository _connections = Substitute.For<IConnectedConnectionRepository>();
    private readonly IUserTokenRepository _tokens = Substitute.For<IUserTokenRepository>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly ConnectedAccessVerifier _verifier;

    public ConnectedAccessVerifierTests()
    {
        _verifier = new ConnectedAccessVerifier(_control, _connections, _tokens, new ConnectedSourceScopeFactory(_scopeFactory));
        _control.GetPolicyAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(ci => new ConnectedSharingPolicy { TenantId = ci.Arg<long>() });
        _control.GetTenantAsync(Provider, Arg.Any<CancellationToken>()).Returns(new ConnectedTenantInfo(Provider, "acme", "Acme", "Active", "Ready", null, false));
        _control.GetTenantAsync(Consumer, Arg.Any<CancellationToken>()).Returns(new ConnectedTenantInfo(Consumer, "me", "Me", "Active", "Ready", null, false));
    }

    private static ConnectedCredential CrossRealm() => new(Provider, 77, true, true, new HashSet<long>());

    private static ConnectedDataShare Approved(string scope = ConnectedDataShareScopes.AllApps, string? apps = null, DateTime? expires = null) => new()
    {
        ProviderTenantId = Provider, ConsumerTenantId = Consumer, Status = ConnectedDataShareStatuses.Approved, Scope = scope,
        AppPublicIdsJson = apps, ExpiresOn = expires,
    };

    private async Task<ConnectedTableException> OpenFails(ConnectedCredential credential)
    {
        var act = () => _verifier.OpenAsync(credential, Guid.NewGuid(), null, Consumer, CancellationToken.None);
        return (await act.Should().ThrowAsync<ConnectedTableException>()).Which;
    }

    [Fact]
    public async Task Open_InboundDisabled_ThrowsInboundDisabled()
    {
        _control.GetPolicyAsync(Consumer, Arg.Any<CancellationToken>()).Returns(new ConnectedSharingPolicy { TenantId = Consumer, InboundEnabled = false });
        (await OpenFails(CrossRealm())).ErrorCode.Should().Be(ConnectedErrorCodes.InboundDisabled);
    }

    [Fact]
    public async Task Open_ProviderSuspended_ThrowsSourceUnavailable()
    {
        _control.GetTenantAsync(Provider, Arg.Any<CancellationToken>()).Returns(new ConnectedTenantInfo(Provider, "acme", "Acme", "Suspended", "Ready", null, false));
        (await OpenFails(CrossRealm())).ErrorCode.Should().Be(ConnectedErrorCodes.SourceUnavailable);
    }

    [Fact]
    public async Task Open_NoShare_ThrowsDataShareNotApproved()
    {
        (await OpenFails(CrossRealm())).ErrorCode.Should().Be(ConnectedErrorCodes.DataShareNotApproved);
    }

    [Fact]
    public async Task Open_ShareStillRequested_ThrowsDataShareNotApproved()
    {
        var share = Approved();
        share.Status = ConnectedDataShareStatuses.Requested;
        _control.GetOpenShareAsync(Provider, Consumer, Arg.Any<CancellationToken>()).Returns(share);
        (await OpenFails(CrossRealm())).Message.Should().Contain("waiting for approval");
    }

    [Fact]
    public async Task Open_ShareExpired_ThrowsDataShareNotApproved()
    {
        _control.GetOpenShareAsync(Provider, Consumer, Arg.Any<CancellationToken>()).Returns(Approved(expires: DateTime.UtcNow.AddDays(-1)));
        (await OpenFails(CrossRealm())).Message.Should().Contain("expired");
    }

    [Fact]
    public async Task Open_ProviderSharingDisabled_ThrowsDataShareNotApproved()
    {
        _control.GetPolicyAsync(Provider, Arg.Any<CancellationToken>())
            .Returns(new ConnectedSharingPolicy { TenantId = Provider, OutboundPolicy = ConnectedOutboundPolicies.Disabled });
        _control.GetOpenShareAsync(Provider, Consumer, Arg.Any<CancellationToken>()).Returns(Approved());
        (await OpenFails(CrossRealm())).ErrorCode.Should().Be(ConnectedErrorCodes.DataShareNotApproved);
    }

    [Fact]
    public async Task Open_ShareForOtherApps_ThrowsDataShareNotApproved()
    {
        _control.GetOpenShareAsync(Provider, Consumer, Arg.Any<CancellationToken>())
            .Returns(Approved(ConnectedDataShareScopes.Apps, $"[\"{Guid.NewGuid()}\"]"));
        (await OpenFails(CrossRealm())).Message.Should().Contain("does not include this app");
    }

    [Fact]
    public async Task Open_DifferentServer_WithoutCrossRegion_ThrowsCrossRegion()
    {
        _control.GetTenantAsync(Provider, Arg.Any<CancellationToken>()).Returns(new ConnectedTenantInfo(Provider, "acme", "Acme", "Active", "Ready", "eu-sql-1", false));
        _control.GetOpenShareAsync(Provider, Consumer, Arg.Any<CancellationToken>()).Returns(Approved());
        (await OpenFails(CrossRealm())).ErrorCode.Should().Be(ConnectedErrorCodes.CrossRegion);
    }

    [Fact]
    public async Task Open_SameRealm_SkipsShareAndRegionGates()
    {
        // No share and no policy rows at all: the same-realm path never asks for them. It gets as far as opening the scope.
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(new ServiceCollection().BuildServiceProvider());
        _scopeFactory.CreateScope().Returns(scope);
        var same = new ConnectedCredential(Consumer, 77, false, true, new HashSet<long>());

        var act = () => _verifier.OpenAsync(same, Guid.NewGuid(), null, Consumer, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(); // the empty test container has no IQueryContext
        await _control.DidNotReceive().GetOpenShareAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveSaved_RevokedToken_MarksConnectionRevoked()
    {
        var connection = new ConnectedConnection
        {
            Id = 5, Name = "c", AuthMode = ConnectedAuthModes.UserToken, UserTokenHash = "h", RunAsUserId = 77, SourceTenantId = Provider,
            Status = ConnectedConnectionStatuses.Active,
        };
        _tokens.GetByHashAsync("h", Arg.Any<CancellationToken>()).Returns(new UserToken { Id = 1, UserId = 77, TenantId = Provider, IsActive = false });

        var act = () => _verifier.ResolveSavedAsync(connection, Consumer, CancellationToken.None);

        (await act.Should().ThrowAsync<ConnectedTableException>()).Which.ErrorCode.Should().Be(ConnectedErrorCodes.ConnectionUnusable);
        await _connections.Received().SetStatusAsync(5, ConnectedConnectionStatuses.Revoked, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveSaved_TokenMovedToOtherRealm_MarksUnavailable()
    {
        var connection = new ConnectedConnection
        {
            Id = 5, Name = "c", AuthMode = ConnectedAuthModes.UserToken, UserTokenHash = "h", RunAsUserId = 77, SourceTenantId = Provider,
            Status = ConnectedConnectionStatuses.Active,
        };
        _tokens.GetByHashAsync("h", Arg.Any<CancellationToken>()).Returns(new UserToken { Id = 1, UserId = 77, TenantId = 99, IsActive = true });

        await FluentActions.Invoking(() => _verifier.ResolveSavedAsync(connection, Consumer, CancellationToken.None)).Should().ThrowAsync<ConnectedTableException>();
        await _connections.Received().SetStatusAsync(5, ConnectedConnectionStatuses.Unavailable, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveSaved_SameRealmConnectionUsedFromAnotherRealm_Throws()
    {
        var connection = new ConnectedConnection
        {
            Id = 5, Name = "c", AuthMode = ConnectedAuthModes.SameRealmUser, OwnerUserId = 7, RunAsUserId = 7, SourceTenantId = Provider,
            Status = ConnectedConnectionStatuses.Active,
        };
        await FluentActions.Invoking(() => _verifier.ResolveSavedAsync(connection, Consumer, CancellationToken.None)).Should().ThrowAsync<ConnectedTableException>();
    }

    [Fact]
    public async Task ResolveUserToken_WrongRealm_Throws()
    {
        _tokens.GetByHashAsync("h", Arg.Any<CancellationToken>()).Returns(new UserToken { Id = 1, UserId = 77, TenantId = 3, IsActive = true });
        await FluentActions.Invoking(() => _verifier.ResolveUserTokenAsync("h", Provider, CancellationToken.None)).Should().ThrowAsync<ConnectedTableException>();
    }

    [Fact]
    public void ShareCoversApp_RespectsScope()
    {
        var app = Guid.NewGuid();
        ConnectedAccessVerifier.ShareCoversApp(Approved(), app).Should().BeTrue();
        ConnectedAccessVerifier.ShareCoversApp(Approved(ConnectedDataShareScopes.Apps, $"[\"{app}\"]"), app).Should().BeTrue();
        ConnectedAccessVerifier.ShareCoversApp(Approved(ConnectedDataShareScopes.Apps, "not json"), app).Should().BeFalse();
    }

    [Fact]
    public void ParseSecurity_DefaultsWhenMissingOrBroken()
    {
        ConnectedAccessVerifier.ParseSecurity(null).AllowNonAdminsToConnect.Should().BeTrue();
        ConnectedAccessVerifier.ParseSecurity("{\"allowNonAdminsToConnect\":false}").AllowNonAdminsToConnect.Should().BeFalse();
        ConnectedAccessVerifier.ParseSecurity("{oops").RequireAppTokens.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "  ", true)]
    [InlineData("eu-sql-1", "EU-SQL-1", true)]
    [InlineData("eu-sql-1", "us-sql-1", false)]
    [InlineData(null, "us-sql-1", false)]
    public void SameServer_ComparesServerRefs(string? a, string? b, bool expected)
    {
        ConnectedAccessVerifier.SameServer(a, b).Should().Be(expected);
    }
}
