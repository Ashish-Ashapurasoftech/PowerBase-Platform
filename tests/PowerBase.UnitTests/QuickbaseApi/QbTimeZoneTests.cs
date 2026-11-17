using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.Application.Import.Qbl;
using PowerBase.Domain.ValueObjects;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Date-time text in XML calls, as a live Quickbase realm stores it for an app in Eastern time (2026-11-16).</summary>
public class QbTimeZoneTests
{
    private static readonly TimeZoneInfo Eastern = QbTimeZone.Resolve("America/New_York")!;

    private static long Ms(DateTime? d) => new DateTimeOffset(d!.Value).ToUnixTimeMilliseconds();

    [Theory]
    [InlineData("03-15-2026 10:00", false, 1773583200000)]      // app-zone wall clock (EDT)
    [InlineData("03-15-2026 10:00 PM", false, 1773626400000)]
    [InlineData("07-15-2026 10:00", false, 1784124000000)]      // DST aware
    [InlineData("2026-03-15 10:00", false, 1773583200000)]
    [InlineData("2026-03-15T10:00:00", false, 1773568800000)]   // ISO with 'T' is UTC
    [InlineData("2026-03-15T10:00:00Z", false, 1773568800000)]
    [InlineData("1773568800000", false, 1773583200000)]          // ms read as app wall clock …
    [InlineData("1773568800000", true, 1773568800000)]           // … unless msInUTC=1
    public void ParseToUtc_MatchesQuickbase(string text, bool msInUtc, long expected) =>
        Ms(QbTimeZone.ParseToUtc(text, Eastern, msInUtc)).Should().Be(expected);

    [Fact]
    public void ParseToUtc_NotADate_IsNull() => QbTimeZone.ParseToUtc("x", Eastern).Should().BeNull();

    [Fact]
    public void Utc_App_KeepsWallClockAsUtc() =>
        Ms(QbTimeZone.ParseToUtc("03-15-2026 10:00", TimeZoneInfo.Utc)).Should().Be(1773568800000);

    [Theory]
    [InlineData("(UTC-05:00) Eastern Time (US & Canada)")]
    [InlineData("America/New_York")]
    [InlineData("Eastern Standard Time")]
    public void Resolve_AcceptsQuickbaseWindowsAndIanaNames(string name) =>
        QbTimeZone.Resolve(name)!.GetUtcOffset(new DateTime(2026, 1, 15)).Should().Be(TimeSpan.FromHours(-5));

    [Fact]
    public void OfApp_DefaultsToUtc()
    {
        QbTimeZone.OfApp(null).Should().Be(TimeZoneInfo.Utc);
        QbTimeZone.OfApp("{\"Date\":{\"FormatString\":\"MM-DD-YYYY\"}}").Should().Be(TimeZoneInfo.Utc);
        QbTimeZone.OfApp("{\"Date\":{\"TimeZone\":\"America/New_York\"}}").GetUtcOffset(new DateTime(2026, 1, 15)).Should().Be(TimeSpan.FromHours(-5));
        AppFormattingSettings.GetTimeZone("not json").Should().Be("UTC");
    }

    [Theory]
    [InlineData("(UTC-05:00) Eastern Time (US & Canada)", "America/New_York")]
    [InlineData("America/Chicago", "America/Chicago")]
    [InlineData(null, null)]
    [InlineData("Nowhere", null)]
    public void QblImport_MapsAppTimeZoneToIana(string? qb, string? expected) =>
        QblAppTableMapper.TimeZoneId(qb).Should().Be(expected);
}
