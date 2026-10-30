using FluentAssertions;
using PowerBase.Application.Common.Formatting;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.UnitTests.Common;

/// <summary>Expected strings are what the Angular AppFormattingService / formatDurationMinutes
/// render for the same input — the two must stay in step.</summary>
public class DisplayValueFormatterTests
{
    private static readonly AppFormattingSettings Defaults = new();   // $ before, 2 decimals, ",", MM-DD-YYYY, "-"
    private static readonly DateTime Today = new(2026, 9, 23);

    private static string? Fmt(object? raw, string type, string? settings = null, AppFormattingSettings? app = null) =>
        DisplayValueFormatter.Format(raw, type, settings, app ?? Defaults, Today);

    [Theory]
    [InlineData(1234567.891, null, "1,234,567.89")]
    [InlineData(1200, null, "1,200.00")]
    [InlineData(-1234.5, null, "-1,234.50")]
    [InlineData(999, null, "999.00")]
    [InlineData(1234.5, "{\"decimals\":0}", "1,235")]
    [InlineData(1234.5, "{\"separator\":\"\"}", "1234.50")]      // field "None" separator is explicit
    [InlineData(1234.5, "{\"separator\":\".\",\"decimals\":1}", "1.234.5")]
    public void Number_UsesFieldOverridesThenAppDefaults(double value, string? settings, string expected) =>
        Fmt((decimal)value, "Number", settings).Should().Be(expected);

    [Fact]
    public void Number_IndianPattern_GroupsByTwosAfterFirstThree()
    {
        var app = new AppFormattingSettings { Number = new NumberFormatSettings { DecimalPlaces = 0, ThousandSeparator = ",", DisplayPattern = "Indian" } };
        Fmt(1234567m, "Number", app: app).Should().Be("12,34,567");
    }

    [Theory]
    [InlineData(1200.5, null, "$1,200.50")]
    [InlineData(1200.5, "{\"symbol\":\"€\",\"position\":\"after\"}", "1,200.50€")]
    [InlineData(1200.5, "{\"symbol\":\"₹\",\"decimals\":0}", "₹1,201")]
    public void Currency_SymbolAndPosition(double value, string? settings, string expected) =>
        Fmt((decimal)value, "Currency", settings).Should().Be(expected);

    [Fact]
    public void Currency_AppLevelSymbolAfter() =>
        Fmt(10m, "Currency", app: new AppFormattingSettings { Currency = new CurrencyFormatSettings { Symbol = " Rs", Position = "After" } })
            .Should().Be("10.00 Rs");

    [Fact]
    public void Percent_AppendsPercentSign() => Fmt(45.5m, "Percent", "{\"decimals\":1}").Should().Be("45.5%");

    [Fact]
    public void Rating_IsWholeStars() => Fmt(4.0000m, "Rating").Should().Be("4");

    [Theory]
    [InlineData(120, null, "2 hrs")]            // Smart
    [InlineData(60, null, "1 hr")]
    [InlineData(1440, null, "1 day")]
    [InlineData(20160, null, "2 wks")]
    [InlineData(90, null, "90 mins")]
    [InlineData(0.5, null, "30 secs")]
    [InlineData(135, "{\"display\":\"HHMM\"}", "2:15")]
    [InlineData(135.5, "{\"display\":\"HHMMSS\"}", "2:15:30")]
    [InlineData(90, "{\"display\":\"Hours\",\"decimals\":1}", "1.5 hrs")]
    [InlineData(2880, "{\"display\":\"Days\"}", "2 days")]
    [InlineData(10080, null, "1 wk")]                                  // one of a unit reads singular
    [InlineData(75, "{\"display\":\"Hours\"}", "1 hr")]
    [InlineData(1440, "{\"display\":\"Days\"}", "1 day")]
    [InlineData(10080, "{\"display\":\"Weeks\"}", "1 wk")]
    [InlineData(1, "{\"display\":\"Minutes\"}", "1 min")]
    [InlineData(210, "{\"display\":\"Hours\"}", "4 hrs")]
    public void Duration_MatchesFrontendDisplays(double minutes, string? settings, string expected) =>
        Fmt((decimal)minutes, "Duration", settings).Should().Be(expected);

    [Fact]
    public void Date_UsesAppFormatAndSeparator()
    {
        var d = new DateTime(2025, 3, 4);
        Fmt(d, "Date").Should().Be("03-04-2025");
        Fmt(d, "Date", app: new AppFormattingSettings { Date = new DateFormatSettings { FormatString = "DD-MM-YYYY", Separator = "/" } })
            .Should().Be("04/03/2025");
        Fmt(d, "Date", app: new AppFormattingSettings { Date = new DateFormatSettings { FormatString = "YYYY-MM-DD" } })
            .Should().Be("2025-03-04");
        Fmt(d, "Date", app: new AppFormattingSettings { Date = new DateFormatSettings { FormatString = "DD-MM-YY" } })
            .Should().Be("04-03-25");
    }

    [Fact]
    public void Date_FieldBehaviorSettings()
    {
        var d = new DateTime(2025, 3, 4);   // a Tuesday, not the current year
        Fmt(d, "Date", "{\"showMonthName\":true}").Should().Be("Mar-04-2025");
        Fmt(d, "Date", "{\"showDayOfWeek\":true}").Should().Be("Tue, 03-04-2025");
        Fmt(d, "Date", "{\"hideYearIfCurrent\":true}").Should().Be("03-04-2025");            // 2025 ≠ current
        Fmt(new DateTime(2026, 3, 4), "Date", "{\"hideYearIfCurrent\":true}").Should().Be("03-04");
    }

    [Fact]
    public void DateTime_ShowsTimeUnlessTurnedOff()
    {
        var dt = new DateTime(2026, 9, 23, 15, 5, 0);
        Fmt(dt, "DateTime").Should().Be("09-23-2026 3:05 PM");
        Fmt(dt, "DateTime", "{\"showTime\":false}").Should().Be("09-23-2026");
        Fmt(dt, "Date").Should().Be("09-23-2026");   // plain Date never shows time
    }

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    public void Boolean_YesNo(bool value, string expected) => Fmt(value, "Boolean").Should().Be(expected);

    // ── Structured / text types: must never come out as raw JSON ──

    [Fact]
    public void Address_JoinsNonEmptyParts()
    {
        Fmt("{\"street1\":\"12 Main Road\",\"street2\":\"\",\"city\":\"Surat\",\"state\":\"Gujarat\",\"zip\":\"395003\",\"country\":\"India\"}", "Address")
            .Should().Be("12 Main Road, Surat, Gujarat, 395003, India");
        Fmt("not json", "Address").Should().Be("not json");   // legacy plain text passes through
    }

    [Fact]
    public void Phone_ShowsJustTheNumber() =>
        Fmt("{\"number\":\"+919825012345\",\"ext\":\"12\"}", "Phone").Should().Be("+919825012345");

    [Theory]
    [InlineData("[\"Red\",\"Blue\"]", "Red, Blue")]
    [InlineData("Red, Blue", "Red, Blue")]      // older comma-separated rows
    [InlineData("[]", null)]                    // nothing selected → blank, skipped by Combined Text
    public void MultiSelect_ListsChoices(string raw, string? expected) =>
        (Fmt(raw, "MultiSelect") is { Length: > 0 } s ? s : null).Should().Be(expected);

    [Theory]
    [InlineData(null, "ravi_shah@example.com")]
    [InlineData("{\"showBeforeAt\":true}", "ravi_shah")]
    [InlineData("{\"showBeforeUnderscore\":true,\"showBeforeAt\":true}", "ravi")]
    [InlineData("{\"linkText\":\"Mail\"}", "Mail")]
    public void Email_FollowsDisplaySettings(string? settings, string expected) =>
        Fmt("ravi_shah@example.com", "Email", settings).Should().Be(expected);

    [Theory]
    [InlineData(null, "https://example.com/a")]
    [InlineData("{\"hideProtocol\":true}", "example.com/a")]
    [InlineData("{\"linkText\":\"Open\"}", "Open")]
    public void Url_FollowsDisplaySettings(string? settings, string expected) =>
        Fmt("https://example.com/a", "Url", settings).Should().Be(expected);

    [Theory]
    [InlineData("14:05", null, "2:05 PM")]
    [InlineData("00:30", null, "12:30 AM")]
    [InlineData("14:05:09", "{\"format\":\"HHMMSS\"}", "2:05:09 PM")]
    [InlineData("09:05", "{\"use24Hour\":true}", "09:05")]
    public void Time_FollowsFormatAnd24HourSetting(string raw, string? settings, string expected) =>
        Fmt(raw, "Time", settings).Should().Be(expected);

    [Fact]
    public void TextAndBlank_PassThrough()
    {
        Fmt("Call client", "Text").Should().Be("Call client");
        Fmt("Open", "SingleSelect").Should().Be("Open");
        Fmt("", "Text").Should().BeNull();
        Fmt(null, "Number").Should().BeNull();
    }

    [Fact]
    public void MalformedSettings_FallBackToDefaults()
    {
        Fmt(1200m, "Currency", "{not json").Should().Be("$1,200.00");
        DisplayValueFormatter.ParseAppFormatting("{bad").Date.FormatString.Should().Be("MM-DD-YYYY");
    }

    // ── Values with no text of their own, shown as the grid shows them (Combined Text over any field type) ──

    private static readonly Dictionary<long, string> Names = new() { [7] = "Asha Patel", [9] = "Ravi Shah" };

    [Theory]
    [InlineData(7L, "User", "Asha Patel")]
    [InlineData("7", "User", "Asha Patel")]
    [InlineData(42L, "User", "42")]                        // no known name: its id
    [InlineData("[\"7\",\"9\"]", "MultiUser", "Asha Patel, Ravi Shah")]
    [InlineData("7, 9", "MultiUser", "Asha Patel, Ravi Shah")]   // older rows: comma-separated
    [InlineData("[]", "MultiUser", null)]
    public void Users_ShowTheirNames(object raw, string type, string? expected) =>
        DisplayValueFormatter.Format(raw, type, null, Defaults, Today, Names).Should().Be(expected);

    [Fact]
    public void UserList_FromAFormula_ShowsNames_AndItsIdsCanBeLookedUp()
    {
        DisplayValueFormatter.Format(new List<string> { "9", "7" }, "MultiUser", null, Defaults, Today, Names).Should().Be("Ravi Shah, Asha Patel");
        DisplayValueFormatter.UserIds("[\"7\",\"x\",\"9\"]", "MultiUser").Should().Equal(7, 9);
        DisplayValueFormatter.UserIds(7L, "User").Should().Equal(7);
        DisplayValueFormatter.UserIds("7", "Text").Should().BeEmpty();
    }

    [Theory]
    [InlineData("""[{"name":"quote.pdf","path":"a/1"},{"name":"logo.png","path":"a/2"}]""", "quote.pdf, logo.png")]
    [InlineData("""{"name":"quote.pdf"}""", "quote.pdf")]
    [InlineData("""[{"path":"a/1"}]""", "attachment")]
    [InlineData("a/1/legacy.bin", "attachment")]          // not JSON: one attachment
    [InlineData("[]", null)]
    public void File_ShowsItsFileNames(string raw, string? expected) =>
        Fmt(raw, "File").Should().Be(expected);

    [Theory]
    [InlineData("<p>Call <b>Asha</b></p><p>Tue&nbsp;&amp; Wed</p>", "Call Asha Tue & Wed")]
    [InlineData("Line one<br>Line two", "Line one Line two")]
    [InlineData("<p> </p>", null)]
    public void RichText_ShowsItsTextWithoutMarkup(string raw, string? expected) =>
        Fmt(raw, "RichText").Should().Be(expected);

    [Theory]
    [InlineData("""{"start":"2026-01-05","end":"2026-03-31"}""", "DateRange", "01-05-2026 – 03-31-2026")]
    [InlineData("""{"start":"2026-01-05T00:00:00"}""", "DateRange", "01-05-2026 – …")]
    [InlineData("""{"start":10.0000,"end":25.5000}""", "NumericRange", "10 – 25.5")]
    [InlineData("{}", "NumericRange", null)]
    public void Range_ShowsBothEnds(string raw, string type, string? expected) =>
        Fmt(raw, type).Should().Be(expected);

    [Fact]
    public void Reference_ShowsItsRecordId()
    {
        Fmt(12L, "Reference").Should().Be("12");
        Fmt(12.0000m, "Reference").Should().Be("12");   // a Reference converted from a Number field is a DECIMAL column
    }
}
