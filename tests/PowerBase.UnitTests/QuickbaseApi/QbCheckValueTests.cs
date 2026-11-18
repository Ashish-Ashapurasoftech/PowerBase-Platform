using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Per-field value checks applied before a Quickbase write reaches the record command.</summary>
public class QbCheckValueTests
{
    private static AppField F(string type, string? settings = null) => new() { TypeCode = type, Fid = 9, Settings = settings };

    [Theory]
    [InlineData("Number", "abc")]
    [InlineData("Currency", "12x")]
    [InlineData("Percent", "lots")]
    [InlineData("Time", "25:99:00")]
    public void BadValues_AreRejected(string type, string value)
    {
        var act = () => QbRecordService.CheckValue(F(type), value);
        act.Should().Throw<QbApiException>();
    }

    [Fact]
    public void NumericText_IsConvertedToNumber() => QbRecordService.CheckValue(F("Number"), "12.5").Should().Be(12.5m);

    [Fact]
    public void Time_IsNormalised() => QbRecordService.CheckValue(F("Time"), "7:05").Should().Be("07:05:00");

    [Theory]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("maybe", false)]                                   // live Quickbase: unreadable → false, no error
    public void Checkbox_Text(string v, bool expected) => QbRecordService.CheckValue(F("Boolean"), v).Should().Be(expected);

    [Fact]
    public void Choice_NotInList_IsKept()
    {
        // Live Quickbase accepts a value that is not a choice (and adds it to the list).
        var field = F("SingleSelect", "{\"options\":[\"Open\",\"Closed\"]}");
        QbRecordService.CheckValue(field, "Other").Should().Be("Other");
    }

    [Fact]
    public void Duration_Text_IsStoredEmpty() => QbRecordService.CheckValue(F("Duration"), "long").Should().BeNull();

    [Fact]
    public void Time_Invalid_HasQuickbaseMessage()
    {
        var act = () => QbRecordService.CheckValue(F("Time"), "25:99:00");
        act.Should().Throw<QbApiException>().Which.Description.Should().Be("The value for Time Of Day field with ID \"9\" is invalid.");
    }

    [Fact]
    public void Text_TooLong_HasQuickbaseMessage()
    {
        var act = () => QbRecordService.CheckValue(F("Text", "{\"validation\":{\"maxLength\":3}}"), "abcd");
        act.Should().Throw<QbApiException>().Which.Description.Should().Be("Value \"abcd\" exceeds the length limit for field with ID \"9\".");
    }

    [Fact]
    public void Lenient_ReturnsNullInsteadOfError() => QbRecordService.CheckValue(F("Number"), "abc", lenient: true).Should().BeNull();

    [Fact]
    public void MultiChoice_NewItems_AreKept() =>
        QbRecordService.CheckValue(F("MultiSelect", "{\"options\":[\"A\",\"B\"]}"), "[\"A\",\"Z\"]").Should().Be("[\"A\",\"Z\"]");

    [Fact]
    public void Choice_FromAnotherTable_IsNotChecked() =>
        QbRecordService.CheckValue(F("SingleSelect", "{\"choiceSource\":{\"fid\":6},\"options\":[\"A\"]}"), "Z").Should().Be("Z");

    [Fact]
    public void Rating_AnyNumber_IsKept() => QbRecordService.CheckValue(F("Rating", "{\"max\":3}"), 99m).Should().Be(99m);

    [Fact]
    public void ReferenceAndRange_AreNotTreatedAsNumbers()
    {
        QbRecordService.CheckValue(F("Reference"), "C-001").Should().Be("C-001");
        QbRecordService.CheckValue(F("Text"), "anything").Should().Be("anything");
        QbRecordService.CheckValue(F("Number"), null).Should().BeNull();
    }
}
