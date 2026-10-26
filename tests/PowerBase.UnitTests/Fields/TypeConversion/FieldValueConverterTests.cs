using FluentAssertions;
using PowerBase.Application.Fields.TypeConversion;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.UnitTests.Fields.TypeConversion;

/// <summary>Scenario numbers refer to the test table in docs/field-type-conversion.md.</summary>
public class FieldValueConverterTests
{
    private static object? Conv(object? raw, string from, string to, FieldValueConversionContext? ctx = null) =>
        FieldValueConverter.Convert(raw, from, to, ctx).Value;

    // ── blanks ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Convert_Blank_StaysBlankAndIsNotLost(string? raw)
    {
        var result = FieldValueConverter.Convert(raw, "Text", "Number");
        result.Value.Should().BeNull();
        result.Lost.Should().BeFalse();
    }

    [Fact]
    public void Convert_Unconvertible_IsMarkedLost()
    {
        var result = FieldValueConverter.Convert("test", "Text", "Number");
        result.Value.Should().BeNull();
        result.Lost.Should().BeTrue();
    }

    // ── Text → Number (scenarios 1–3) ──

    [Theory]
    [InlineData("test12test12", 12)]
    [InlineData("test12test", 12)]
    [InlineData("test1", 1)]
    [InlineData("test12_27", 12)]
    [InlineData("Mahesh - 4000", 4000)]
    [InlineData("$1,200.50", 1200.50)]
    [InlineData("-7.5 kg", -7.5)]
    [InlineData("50%", 50)]
    [InlineData("12,34", 12)]
    public void TextToNumber_TakesFirstNumericOccurrence(string raw, double expected) =>
        Conv(raw, "Text", "Number").Should().Be((decimal)expected);

    [Theory]
    [InlineData("test")]
    [InlineData("abc-def")]
    [InlineData("999999999999999999999")]
    public void TextToNumber_NoStorableNumber_IsBlank(string raw) =>
        Conv(raw, "Text", "Number").Should().BeNull();

    // Date/Time/User/File → Number: first number in the value as shown.
    [Fact]
    public void DateToNumber_FirstNumberAsShown() => Conv(new DateTime(2026, 10, 2), "Date", "Number").Should().Be(10m);

    [Fact]
    public void DateToNumber_DayFirstApp_GivesDay()
    {
        var ctx = new FieldValueConversionContext
        {
            AppFormatting = new AppFormattingSettings { Date = new DateFormatSettings { FormatString = "DD-MM-YYYY" } },
        };
        Conv(new DateTime(2026, 10, 2), "Date", "Number", ctx).Should().Be(2m);
    }

    [Fact]
    public void DateTimeToCurrency_FirstNumberAsShown() =>
        Conv(new DateTime(2026, 10, 29, 13, 30, 0), "DateTime", "Currency").Should().Be(10m);

    [Fact]
    public void TimeToNumber_Hour() => Conv(new TimeSpan(13, 30, 0), "Time", "Number").Should().Be(1m);

    [Fact]
    public void FileToNumber_FirstNumberInName() => Conv("[{\"name\":\"invoice 2024.pdf\"}]", "File", "Number").Should().Be(2024m);

    [Fact]
    public void UserToNumber_FirstNumberInEmail()
    {
        var ctx = new FieldValueConversionContext { UserEmailsById = new Dictionary<long, string> { [5] = "user12@x.com", [6] = "mahesh@x.com" } };
        Conv("5", "User", "Number", ctx).Should().Be(12m);
        Conv("6", "User", "Number", ctx).Should().BeNull();
    }

    [Fact]
    public void FormulaDateToNumber_FirstNumberAsShown() => Conv("2026-10-02", "Formula_Date", "Number").Should().Be(10m);

    [Fact]
    public void FormulaTextToNumber_TakesFirstNumber() =>
        Conv("Mahesh - 4000", "Formula_Text", "Currency").Should().Be(4000m);

    [Fact]
    public void RichTextToNumber_StripsHtmlFirst() =>
        Conv("<p style=\"color:red\">Total <b>25</b></p>", "RichText", "Number").Should().Be(25m);

    // ── Numeric family ──

    [Fact]
    public void NumberToPercent_ScalesLikeDisplayAs() => Conv(1.15m, "Number", "Percent").Should().Be(115m);

    [Fact]
    public void PercentToNumber_ScalesBack() => Conv(115m, "Percent", "Number").Should().Be(1.15m);

    [Fact]
    public void CurrencyToNumber_KeepsValue() => Conv(4000m, "Currency", "Number").Should().Be(4000m);

    // ── Boolean (scenarios 6–8) ──

    [Theory]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData(" 1 ", true)]
    [InlineData("False", false)]
    [InlineData("no", false)]
    [InlineData("0", false)]
    public void TextToBoolean_AcceptsTrueFalseYesNoOneZero(string raw, bool expected) =>
        Conv(raw, "Text", "Boolean").Should().Be(expected);

    [Theory]
    [InlineData("maybe")]
    [InlineData("checked")]
    [InlineData("2")]
    public void TextToBoolean_OtherValues_AreBlank(string raw) => Conv(raw, "Text", "Boolean").Should().BeNull();

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void NumberToBoolean_OnlyZeroOrOne(int raw, bool expected) => Conv((decimal)raw, "Number", "Boolean").Should().Be(expected);

    [Fact]
    public void NumberToBoolean_Five_IsBlank() => Conv(5m, "Number", "Boolean").Should().BeNull();

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    public void BooleanToText_YesNo(bool raw, string expected) => Conv(raw, "Boolean", "Text").Should().Be(expected);

    [Fact]
    public void BooleanToNumber_OneZero() => Conv(true, "Boolean", "Number").Should().Be(1m);

    // ── Any → Text ──

    [Fact]
    public void NumberToText_PlainInvariantNoRounding() => Conv(1200.5m, "Currency", "Text").Should().Be("1200.5");

    [Fact]
    public void DateToText_UsesAppFormat() => Conv(new DateTime(2026, 10, 2), "Date", "Text").Should().Be("10-02-2026");

    [Fact]
    public void DateTimeToText_IncludesTime() =>
        Conv(new DateTime(2026, 10, 29, 1, 30, 0), "DateTime", "Text").Should().Be("10-29-2026 1:30 AM");

    [Fact]
    public void TimeToText_TwelveHour() => Conv(new TimeSpan(0, 30, 0), "Time", "Text").Should().Be("12:30 AM");

    [Fact]
    public void DurationToText_Smart() => Conv(4320, "Duration", "Text").Should().Be("3 days");

    [Fact]
    public void PhoneToText_NumberAndExtension() =>
        Conv("{\"number\":\"555-123-4567\",\"ext\":\"12\"}", "Phone", "Text").Should().Be("555-123-4567 x12");

    [Fact]
    public void FileToText_FileNames() =>
        Conv("[{\"name\":\"a.pdf\"},{\"name\":\"b (4).jpg\"}]", "File", "Text").Should().Be("a.pdf, b (4).jpg");

    [Fact]
    public void RichTextToText_KeepsHtml() =>
        Conv("<h2><u>Mahesh</u></h2>", "RichText", "Text").Should().Be("<h2><u>Mahesh</u></h2>");

    [Fact]
    public void MultiLineToText_CollapsesLines() => Conv("testing\r\ntesting1", "TextMultiLine", "Text").Should().Be("testing testing1");

    [Fact]
    public void MultiLineToMultiLine_KeepsLines() => Conv("a\nb", "Text", "TextMultiLine").Should().Be("a\nb");

    [Fact]
    public void MultiLineToRichText_LineBreaksBecomeBr() => Conv("a\nb", "TextMultiLine", "RichText").Should().Be("a<br>b");

    [Fact]
    public void TextToRichText_EncodesMarkup() =>
        Conv("<img src=x onerror=alert(1)> & co", "Text", "RichText").Should().Be("&lt;img src=x onerror=alert(1)&gt; &amp; co");

    [Fact]
    public void TextToSingleSelect_SingleLine() => Conv("testing\ntesting1", "TextMultiLine", "SingleSelect").Should().Be("testing testing1");

    // ── Dates (scenarios 4–5) ──

    [Theory]
    [InlineData("abc")]
    [InlineData("13-45-2026")]
    [InlineData("12")]
    public void TextToDate_Unparseable_IsBlankNeverEpoch(string raw) => Conv(raw, "Text", "Date").Should().BeNull();

    [Theory]
    [InlineData("10-02-2026", 2026, 10, 2)]
    [InlineData("due 10/02/2026 please", 2026, 10, 2)]
    [InlineData("2026-10-02", 2026, 10, 2)]
    [InlineData("Oct 2, 2026", 2026, 10, 2)]
    [InlineData("2nd October 2026", 2026, 10, 2)]
    [InlineData("25-12-2026", 2026, 12, 25)]   // impossible as MM-DD → read as DD-MM
    [InlineData("10-02-26", 2026, 10, 2)]
    public void TextToDate_FirstDateInAppOrder(string raw, int y, int m, int d) =>
        Conv(raw, "Text", "Date").Should().Be(new DateTime(y, m, d));

    [Fact]
    public void TextToDate_DayFirstApp_ReadsDayFirst()
    {
        var ctx = new FieldValueConversionContext
        {
            AppFormatting = new AppFormattingSettings { Date = new DateFormatSettings { FormatString = "DD-MM-YYYY" } },
        };
        Conv("10-02-2026", "Text", "Date", ctx).Should().Be(new DateTime(2026, 2, 10));
    }

    [Fact]
    public void TextToDate_FirstOccurrenceWins() =>
        Conv("from 2026-01-05 to 2026-02-07", "Text", "Date").Should().Be(new DateTime(2026, 1, 5));

    [Fact]
    public void TextToDateTime_KeepsTrailingTime() =>
        Conv("10-29-2026 01:30 PM", "Text", "DateTime").Should().Be(new DateTime(2026, 10, 29, 13, 30, 0));

    [Fact]
    public void TextToDate_DropsTime() =>
        Conv("10-29-2026 01:30 PM", "Text", "Date").Should().Be(new DateTime(2026, 10, 29));

    [Fact]
    public void DateTimeToDate_DatePart() =>
        Conv(new DateTime(2026, 10, 29, 13, 30, 0), "DateTime", "Date").Should().Be(new DateTime(2026, 10, 29));

    [Fact]
    public void DateToDateTime_Midnight() =>
        Conv(new DateTime(2026, 10, 29), "Date", "DateTime").Should().Be(new DateTime(2026, 10, 29));

    [Fact]
    public void DateToText_RoundTripsBackToDate()
    {
        var text = Conv(new DateTime(2026, 10, 2), "Date", "Text");
        Conv(text, "Text", "Date").Should().Be(new DateTime(2026, 10, 2));
    }

    [Fact]
    public void NumberToDate_IsBlank() => Conv(45000m, "Number", "Date").Should().BeNull();

    // ── Time ──

    [Theory]
    [InlineData("12:30 am", 0, 30, 0)]
    [InlineData("at 1:05pm today", 13, 5, 0)]
    [InlineData("23:59:59", 23, 59, 59)]
    [InlineData("9 AM", 9, 0, 0)]
    public void TextToTime_FirstTime(string raw, int h, int m, int s) =>
        Conv(raw, "Text", "Time").Should().Be(new TimeSpan(h, m, s));

    [Theory]
    [InlineData("25:00")]
    [InlineData("13:00 pm")]
    [InlineData("noon")]
    public void TextToTime_Invalid_IsBlank(string raw) => Conv(raw, "Text", "Time").Should().BeNull();

    [Fact]
    public void DateTimeToTime_TimePart() =>
        Conv(new DateTime(2026, 1, 1, 14, 15, 0), "DateTime", "Time").Should().Be(new TimeSpan(14, 15, 0));

    // ── Duration (scenario 13) ──

    [Theory]
    [InlineData("3 days", 4320)]
    [InlineData("2h 30m", 150)]
    [InlineData("2h30m", 150)]
    [InlineData("1 wk 2 days", 12960)]
    [InlineData("1:30", 90)]
    [InlineData("1:30:30", 91)]
    [InlineData("90 mins", 90)]
    [InlineData("45", 45)]
    public void TextToDuration_Minutes(string raw, long expected) => Conv(raw, "Text", "Duration").Should().Be(expected);

    [Fact]
    public void TextToDuration_BareNumberUsesTargetDisplayUnit()
    {
        var ctx = new FieldValueConversionContext { TargetSettingsJson = "{\"display\":\"Days\"}" };
        Conv("3", "Text", "Duration", ctx).Should().Be(4320L);
    }

    [Fact]
    public void NumberToDuration_UsesTargetDisplayUnit()
    {
        var ctx = new FieldValueConversionContext { TargetSettingsJson = "{\"display\":\"Hours\"}" };
        Conv(2m, "Number", "Duration", ctx).Should().Be(120L);
    }

    [Fact]
    public void DurationToNumber_UsesSourceDisplayUnit()
    {
        var ctx = new FieldValueConversionContext { SourceSettingsJson = "{\"display\":\"Days\"}" };
        Conv(4320, "Duration", "Number", ctx).Should().Be(3m);
    }

    [Fact]
    public void TextToDuration_NoNumber_IsBlank() => Conv("soon", "Text", "Duration").Should().BeNull();

    // ── Email / Phone / Url ──

    [Fact]
    public void TextToEmail_FirstEmail() => Conv("contact: Mahesh@Example.com, x@y.io", "Text", "Email").Should().Be("Mahesh@Example.com");

    [Fact]
    public void TextToEmail_NoEmail_IsBlank() => Conv("mahesh", "Text", "Email").Should().BeNull();

    [Fact]
    public void TextToPhone_StoresJson() => Conv("call +1 (555) 123-4567 now", "Text", "Phone").Should().Be("{\"number\":\"+1 (555) 123-4567\"}");

    [Fact]
    public void TextToPhone_WithExtension() =>
        Conv("555-123-4567 ext 89", "Text", "Phone").Should().Be("{\"number\":\"555-123-4567\",\"ext\":\"89\"}");

    [Theory]
    [InlineData("12345")]
    [InlineData("mahesh")]
    public void TextToPhone_TooFewDigits_IsBlank(string raw) => Conv(raw, "Text", "Phone").Should().BeNull();

    [Fact]
    public void TextToUrl_KeepsValue() => Conv(" www.google.com ", "Text", "Url").Should().Be("www.google.com");

    [Fact]
    public void UrlToText_KeepsValue() => Conv("http://fw", "Url", "Text").Should().Be("http://fw");

    // ── User (scenario 9) ──

    private static readonly FieldValueConversionContext UserCtx = new()
    {
        UserIdsByEmail = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["mahesh@x.com"] = 17 },
        UserEmailsById = new Dictionary<long, string> { [17] = "mahesh@x.com" },
    };

    [Fact]
    public void TextToUser_MatchesAppUserByEmailCaseInsensitive() => Conv("Mahesh@X.com", "Text", "User", UserCtx).Should().Be("17");

    [Fact]
    public void TextToUser_QuotedEmail_Matches() => Conv("'mahesh@x.com'", "Text", "User", UserCtx).Should().Be("17");

    [Fact]
    public void TextToUser_UnknownEmail_IsBlank() => Conv("other@x.com", "Text", "User", UserCtx).Should().BeNull();

    [Fact]
    public void TextToUser_NotAnEmail_IsBlank() => Conv("Mahesh Darji", "Text", "User", UserCtx).Should().BeNull();

    [Fact]
    public void UserToText_Email() => Conv("17", "User", "Text", UserCtx).Should().Be("mahesh@x.com");

    [Fact]
    public void UserToText_ResolvedPair_Email() => Conv("17|Darji, Mahesh", "User", "Text", UserCtx).Should().Be("mahesh@x.com");

    [Fact]
    public void UserToText_UnknownUser_KeepsIdText() => Conv("99", "User", "Text", UserCtx).Should().Be("99");

    [Fact]
    public void UserToEmail_Email() => Conv(17L, "User", "Email", UserCtx).Should().Be("mahesh@x.com");

    [Fact]
    public void UserTextUser_RoundTrips()
    {
        var text = Conv("17", "User", "Text", UserCtx);
        Conv(text, "Text", "User", UserCtx).Should().Be("17");
    }

    [Fact]
    public void FormulaUserToText_Email() => Conv("17", "Formula_User", "Text", UserCtx).Should().Be("mahesh@x.com");

    // ── File target ──

    [Fact]
    public void AnyToFile_IsBlankAndLost() => FieldValueConverter.Convert("Mahesh", "Text", "File").Should().Be(new ConvertedValue(null, true));

    // ── Formula sources ──

    [Fact]
    public void FormulaDurationTimeSpan_ToText() => Conv(TimeSpan.FromDays(3), "Formula_Duration", "Text").Should().Be("3 days");

    [Fact]
    public void FormulaBoolToText() => Conv(true, "Formula_Bool", "Text").Should().Be("Yes");

    [Fact]
    public void FormulaNumberToText() => Conv(123m, "Formula_Number", "Text").Should().Be("123");

    // Formula → its own scalar twin, with values in FormulaRawValue's form.
    [Fact]
    public void FormulaBoolToBoolean() => Conv(true, "Formula_Bool", "Boolean").Should().Be(true);

    [Fact]
    public void FormulaNumberToNumber() => Conv(12.5m, "Formula_Number", "Number").Should().Be(12.5m);

    [Fact]
    public void FormulaDateToDate() => Conv("2026-10-02", "Formula_Date", "Date").Should().Be(new DateTime(2026, 10, 2));

    [Fact]
    public void FormulaDateTimeToDateTime() =>
        Conv("2026-10-02T13:30:00", "Formula_DateTime", "DateTime").Should().Be(new DateTime(2026, 10, 2, 13, 30, 0));

    [Fact]
    public void FormulaTimeToTime() => Conv("13:30:00", "Formula_Time", "Time").Should().Be(new TimeSpan(13, 30, 0));

    [Fact]
    public void FormulaDurationToDuration() => Conv(90.4m, "Formula_Duration", "Duration").Should().Be(90L);

    [Fact]
    public void FormulaUserToUser() => Conv("17", "Formula_User", "User", UserCtx).Should().Be("17");

    [Fact]
    public void FormulaPhoneToPhone() => Conv("555-123-4567", "Formula_Phone", "Phone").Should().Be("{\"number\":\"555-123-4567\"}");

    [Fact]
    public void FormulaTextToText() => Conv("Mahesh - 4000", "Formula_Text", "Text").Should().Be("Mahesh - 4000");
}
