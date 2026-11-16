using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Value rules from developer.quickbase.com "Field type details".</summary>
public class QbFieldValueRulesTests
{
    [Fact]
    public void Checkbox_EmptyIsFalse()
    {
        QbValueFormatter.Format(null, "checkbox").Should().Be(false);
        QbValueFormatter.Format(true, "checkbox").Should().Be(true);
    }

    [Fact]
    public void Percent_IsFraction()
    {
        QbValueFormatter.Format(75m, "percent").Should().Be(0.75m);
        QbValueFormatter.Format(115L, "percent").Should().Be(1.15m);
    }

    [Fact]
    public void Address_IsOneString()
    {
        QbValueFormatter.Format("{\"street1\":\"10 main street\",\"city\":\"Cambridge\",\"state\":\"MA\",\"zip\":\"02139\"}", "address")
            .Should().Be("10 main street, Cambridge, MA 02139");
        QbValueFormatter.Format("{}", "address").Should().BeNull();
    }

    [Fact]
    public void TimeOfDay_Is24Hour()
    {
        QbValueFormatter.Format(new TimeSpan(16, 30, 0), "timeofday").Should().Be("16:30:00");
    }

    [Fact]
    public void File_ReservedByOnlyWhenReserved()
    {
        var plain = (IDictionary<string, object?>)QbValueFormatter.Format("{\"name\":\"a.pdf\",\"path\":\"/files/x.pdf\",\"uploadedOn\":\"2026-01-01T00:00:00Z\",\"uploadedBy\":\"Jo\"}", "file", "/apps/1/files/download")!;
        plain.Keys.Should().Equal("url", "versions");
        var reserved = (IDictionary<string, object?>)QbValueFormatter.Format("{\"name\":\"a.pdf\",\"path\":\"/files/x.pdf\",\"reservation\":{\"userName\":\"Jo\"}}", "file", "/apps/1/files/download")!;
        reserved.Keys.Should().Equal("url", "reservedBy", "versions");
    }

    [Theory]
    [InlineData("today", "2026-11-04")]
    [InlineData("3 days ago", "2026-11-01")]
    [InlineData("1 day from now", "2026-11-05")]
    [InlineData("2019-12-18", "2019-12-18")]
    public void InputDate_Keywords(string input, string expected)
    {
        QbInputValues.Date(input, new DateTime(2026, 11, 4, 15, 0, 0, DateTimeKind.Utc)).Should().Be(expected);
    }

    [Theory]
    [InlineData("2019-12-18T12:00:00.000-04:00", "2019-12-18T16:00:00Z")]
    [InlineData("2019-12-18T08:00:00Z", "2019-12-18T08:00:00Z")]
    [InlineData("2019-12-18T12:00:00", "2019-12-18T12:00:00Z")]
    [InlineData("now", "2026-11-04T15:00:00Z")]
    public void InputDateTime_ZonesAndKeywords(string input, string expected)
    {
        QbInputValues.DateTime(input, new DateTime(2026, 11, 4, 15, 0, 0, DateTimeKind.Utc)).Should().Be(expected);
    }
}
