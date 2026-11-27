using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.API.QuickbaseApi.Common;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbIdResolverTests
{
    [Theory]
    [InlineData("34abb534-30c2-f111-bbff-002324be71d7", QbIdResolver.IdKind.PublicId)]
    [InlineData("34ABB53430C2F111BBFF002324BE71D7", QbIdResolver.IdKind.PublicId)]
    [InlineData("{34abb534-30c2-f111-bbff-002324be71d7}", QbIdResolver.IdKind.PublicId)]
    [InlineData("5", QbIdResolver.IdKind.Number)]
    [InlineData(" 102803 ", QbIdResolver.IdKind.Number)]
    [InlineData("0", QbIdResolver.IdKind.Invalid)]
    [InlineData("-5", QbIdResolver.IdKind.Invalid)]
    [InlineData("+5", QbIdResolver.IdKind.Invalid)]
    [InlineData("5.0", QbIdResolver.IdKind.Invalid)]
    [InlineData("1e3", QbIdResolver.IdKind.QuickbaseId)]
    [InlineData("bck7gp3q2", QbIdResolver.IdKind.QuickbaseId)]
    [InlineData("bv77y482c", QbIdResolver.IdKind.QuickbaseId)]
    [InlineData(" btdebznhc ", QbIdResolver.IdKind.QuickbaseId)]
    [InlineData("buzkbek8b", QbIdResolver.IdKind.QuickbaseId)]
    [InlineData("bv5j-unvd", QbIdResolver.IdKind.Invalid)]
    [InlineData("bv5j'unvd", QbIdResolver.IdKind.Invalid)]
    [InlineData("99999999999999999999", QbIdResolver.IdKind.Invalid)]
    [InlineData("", QbIdResolver.IdKind.Missing)]
    [InlineData(null, QbIdResolver.IdKind.Missing)]
    public void Classify_GuidNumberOrInvalid(string? raw, QbIdResolver.IdKind expected)
    {
        QbIdResolver.Classify(raw, out _, out _).Should().Be(expected);
    }

    private static QbIdResolver Resolver(IAppRepository apps, IAppTableRepository tables) => new(apps, tables,
        Substitute.For<IReportRepository>(), Substitute.For<IAppRoleRepository>(), Substitute.For<IUserRepository>(),
        Substitute.For<ITenantRepository>(), Substitute.For<IImportDefinitionRepository>());

    [Fact]
    public async Task AppAsync_QuickbaseId_LooksUpQbAppId()
    {
        var id = Guid.NewGuid();
        var apps = Substitute.For<IAppRepository>();
        apps.FindPublicIdByQbAppIdAsync("bv77y482c", Arg.Any<CancellationToken>()).Returns(id);
        (await Resolver(apps, Substitute.For<IAppTableRepository>()).AppAsync(" bv77y482c ", "appId", default)).Should().Be(id);
    }

    [Fact]
    public async Task TableAsync_QuickbaseId_LooksUpQbTableId()
    {
        var id = Guid.NewGuid();
        var tables = Substitute.For<IAppTableRepository>();
        tables.FindPublicIdByQbTableIdAsync("btdebznhc", Arg.Any<CancellationToken>()).Returns(id);
        (await Resolver(Substitute.For<IAppRepository>(), tables).TableAsync("btdebznhc", "tableId", default)).Should().Be(id);
    }

    [Fact]
    public async Task UnknownQuickbaseId_IsQuickbaseInvalidRequest()
    {
        var apps = Substitute.For<IAppRepository>();
        apps.FindPublicIdByQbAppIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);
        var tables = Substitute.For<IAppTableRepository>();
        tables.FindPublicIdByQbTableIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);
        var r = Resolver(apps, tables);
        (await FluentActions.Awaiting(() => r.TableAsync("bzzzzzzzz", "tableId", default)).Should().ThrowAsync<QbApiException>())
            .Which.StatusCode.Should().Be(400);
        (await FluentActions.Awaiting(() => r.AppAsync("bzzzzzzzz", "appId", default)).Should().ThrowAsync<QbApiException>())
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public void Classify_Number_ReturnsValue()
    {
        QbIdResolver.Classify("42", out _, out var n);
        n.Should().Be(42);
    }
}
