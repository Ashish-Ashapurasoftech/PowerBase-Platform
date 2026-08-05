using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Commands.InvokeButtonAction;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Records;

/// <summary>
/// End-to-end Link Expiration coverage using the REAL <see cref="ActionButtonValueResolver"/>
/// and the REAL settings JSON the Angular client persists — no hand-built settings objects,
/// no mocked value resolution. This is what isolates "the timestamp format the user typed"
/// from "the value never reached the server".
/// </summary>
public class LinkExpirationEndToEndTests
{
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IRecordWriteService _writeService = Substitute.For<IRecordWriteService>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();
    private readonly IFormulaRuntimeContext _runtime = Substitute.For<IFormulaRuntimeContext>();
    private readonly IAppRepository _appRepo = Substitute.For<IAppRepository>();

    private readonly AppTable _table;
    private readonly Guid _recordId = Guid.NewGuid();
    private const int ButtonFid = 10;
    private const int TargetFid = 20;

    public LinkExpirationEndToEndTests()
    {
        _table = new AppTable { Id = 1, PublicId = Guid.NewGuid(), Name = "T", AppId = 1 };
        _tableRepo.GetByPublicIdAsync(_table.PublicId, Arg.Any<CancellationToken>()).Returns(_table);
        _appRepo.GetPublicIdByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        _recordRepo.GetByPublicIdAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), _recordId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?>());

        _enforcer.EnsureButtonWriteAllowedAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlySet<long>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _writeService.ApplyAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyDictionary<long, object?>>()));
    }

    /// <summary>The real resolver — Data-kind ValueSources short-circuit before touching
    /// any repository, so the mocked deps are never exercised for these cases.</summary>
    private ActionButtonValueResolver RealResolver() => new(
        new FormulaEngine(), _queryContext, _runtime, _appRepo, _tableRepo, _fieldRepo, _recordRepo);

    private InvokeButtonActionCommandHandler CreateSut() => new(
        _tableRepo, _fieldRepo, _recordRepo, _enforcer, _writeService, RealResolver(), _queryContext);

    /// <summary>The exact settings JSON shape the Angular client persists (camelCase).</summary>
    private void SetupButtonWithRawSettingsJson(string settingsJson)
    {
        var button = new AppField
        {
            Id = 100, Fid = ButtonFid, TypeCode = "ActionButton", Name = "Btn", Label = "Btn",
            Settings = settingsJson,
        };
        var target = new AppField { Id = TargetFid, Fid = TargetFid, TypeCode = "Text", Name = "Target" };
        _fieldRepo.ListByTableAsync(_table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField> { button, target });
    }

    private static string SettingsJson(string startData, int minutes) => JsonSerializer.Serialize(new
    {
        variant = "Data",
        addData = new[] { new { targetFid = TargetFid, value = new { kind = "data", data = "written" } } },
        linkExpiration = new { start = new { kind = "data", data = startData }, minutes },
    });

    private InvokeButtonActionCommand Command() =>
        new(_table.PublicId, _recordId, ButtonFid, null, null, null, null, null, null, null);

    // ── Formats a user could plausibly type into the Start box ────────────────────

    public static TheoryData<string> ExpiredStartFormats() => new()
    {
        // ISO-8601 with explicit UTC designator — what was recommended.
        "{0:yyyy-MM-ddTHH:mm:ss}Z",
        // ISO-8601, no timezone designator at all (most natural thing to type).
        "{0:yyyy-MM-ddTHH:mm:ss}",
        // Space instead of 'T'.
        "{0:yyyy-MM-dd HH:mm:ss}",
        // No seconds.
        "{0:yyyy-MM-dd HH:mm}",
        // With an explicit +00:00 offset.
        "{0:yyyy-MM-ddTHH:mm:ss}+00:00",
    };

    [Theory]
    [MemberData(nameof(ExpiredStartFormats))]
    public async Task StartWellInThePast_Expires_ForEveryPlausibleTimestampFormat(string format)
    {
        // 10 minutes ago, rendered in the format under test; window is only 2 minutes.
        var start = DateTime.UtcNow.AddMinutes(-10);
        var startData = string.Format(CultureInfo.InvariantCulture, format, start);

        SetupButtonWithRawSettingsJson(SettingsJson(startData, minutes: 2));

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(Command()))
            .Should().ThrowAsync<LinkExpiredException>(
                $"a Start of '{startData}' is 10 minutes old and the window is 2 minutes");

        await _writeService.DidNotReceive().ApplyAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(ExpiredStartFormats))]
    public async Task StartJustNow_StillValid_ForEveryPlausibleTimestampFormat(string format)
    {
        // 1 minute ago with a 30-minute window — comfortably inside.
        var start = DateTime.UtcNow.AddMinutes(-1);
        var startData = string.Format(CultureInfo.InvariantCulture, format, start);

        SetupButtonWithRawSettingsJson(SettingsJson(startData, minutes: 30));

        var sut = CreateSut();
        var result = await sut.HandleAsync(Command());

        result.UpdatedFields[TargetFid].Should().Be("written",
            $"a Start of '{startData}' is 1 minute old and the window is 30 minutes");
    }

    [Fact]
    public async Task LocalWallClockTimestamp_IsTreatedAsUtc_NotShiftedByServerTimeZone()
    {
        // Regression guard for the timezone trap: a bare timestamp with no offset must be
        // interpreted as UTC. If it were parsed as server-local and converted, a machine in
        // e.g. IST (UTC+5:30) would read a 10-minute-old stamp as 5h20m in the FUTURE and
        // wrongly treat an expired button as still valid.
        var start = DateTime.UtcNow.AddMinutes(-10);
        var startData = start.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        SetupButtonWithRawSettingsJson(SettingsJson(startData, minutes: 2));

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(Command()))
            .Should().ThrowAsync<LinkExpiredException>();
    }

    [Fact]
    public async Task UnparseableStartText_FailsClosed()
    {
        SetupButtonWithRawSettingsJson(SettingsJson("not a timestamp", minutes: 2));

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(Command()))
            .Should().ThrowAsync<LinkExpiredException>();
    }

    [Fact]
    public async Task BlankStart_FailsClosed()
    {
        SetupButtonWithRawSettingsJson(SettingsJson("", minutes: 2));

        var sut = CreateSut();
        await sut.Invoking(s => s.HandleAsync(Command()))
            .Should().ThrowAsync<LinkExpiredException>();
    }
}
