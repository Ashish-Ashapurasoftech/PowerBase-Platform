using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Connections.Queries.GetConnections;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Connections;

public class GetConnectionsQueryHandlerTests
{
    private const long LoggedInUserId = 11;

    private readonly IPipelineAccountRepository _accountRepo = Substitute.For<IPipelineAccountRepository>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly GetConnectionsQueryHandler _handler;

    public GetConnectionsQueryHandlerTests()
    {
        _queryContext.UserId.Returns(LoggedInUserId);
        _handler = new GetConnectionsQueryHandler(_accountRepo, _queryContext);
    }

    private static PipelineAccount Account(string name, string subdomain, bool isActive = true) => new()
    {
        Id = 5,
        PublicId = Guid.NewGuid(),
        TenantId = 1,
        CreatedByUserId = LoggedInUserId,
        Name = name,
        AuthMode = PipelineAccountAuthModes.UserToken,
        Subdomain = subdomain,
        TargetTenantId = 42,
        TargetUserId = 77,
        TokenHash = new string('a', 64),
        TokenPrefix = "pb_ut_abcd…",
        Status = PipelineAccountStatuses.Active,
        IsActive = isActive,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task HandleAsync_ScopesTheListToTheLoggedInUser()
    {
        _accountRepo.ListForUserAsync(LoggedInUserId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineAccount> { Account("owner@acme.test (acme)", "acme") });

        var result = await _handler.HandleAsync(new GetConnectionsQuery());

        result.Items.Should().HaveCount(1);
        await _accountRepo.Received(1).ListForUserAsync(LoggedInUserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoSavedAccounts_ReturnsEmptyList()
    {
        _accountRepo.ListForUserAsync(LoggedInUserId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineAccount>());

        var result = await _handler.HandleAsync(new GetConnectionsQuery());

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ReturnsDisplaySafeRowsWithoutAnyCredentialMaterial()
    {
        var account = Account("owner@acme.test (acme)", "acme");
        _accountRepo.ListForUserAsync(LoggedInUserId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineAccount> { account });

        var result = await _handler.HandleAsync(new GetConnectionsQuery());

        var dto = result.Items.Single();
        dto.PublicId.Should().Be(account.PublicId);
        dto.Name.Should().Be("owner@acme.test (acme)");
        dto.Subdomain.Should().Be("acme");
        dto.TokenPrefix.Should().Be("pb_ut_abcd…");

        JsonSerializer.Serialize(result).Should().NotContain(account.TokenHash!);
    }

    [Fact]
    public async Task HandleAsync_DeactivatedAccount_IsReportedAsUnavailable()
    {
        _accountRepo.ListForUserAsync(LoggedInUserId, Arg.Any<CancellationToken>())
            .Returns(new List<PipelineAccount> { Account("stale (acme)", "acme", isActive: false) });

        var result = await _handler.HandleAsync(new GetConnectionsQuery());

        result.Items.Single().Status.Should().Be(PipelineAccountStatuses.Unavailable);
    }
}
