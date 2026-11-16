using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.API.QuickbaseApi.Xml;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Fixes from the line-by-line review of every Quickbase-compatible file.</summary>
public class QbDeepReviewFixTests
{
    [Theory]
    [InlineData("[\"VIP\", \"Loyal\"]", "[\"VIP\",\"Loyal\"]")]
    [InlineData("{\"street1\": \"1 Main\", \"city\": \"Pune\"}", "{\"city\":\"Pune\",\"street1\":\"1 Main\"}")]
    [InlineData("{\"start\":10.0000,\"end\":20}", "{\"start\":10,\"end\":20.00}")]
    [InlineData("{\"start\":\"2026-01-01T00:00:00\",\"end\":null}", "{\"start\":\"2026-01-01\",\"end\":null}")]
    public void SameValue_JsonValuesCompareByContent(string stored, string sent) =>
        QbRecordService.SameValue(stored, sent).Should().BeTrue();

    [Fact]
    public void SameValue_StoredRangeDictionary_EqualsSentRange()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"start\":1,\"end\":2}");
        var stored = new Dictionary<string, object?> { ["start"] = 1.0000m, ["end"] = 2.0000m };
        QbRecordService.SameValue(stored, doc.RootElement.Clone()).Should().BeTrue();
    }

    [Fact]
    public void SameValue_DifferentLists_AreDifferent() =>
        QbRecordService.SameValue("[\"VIP\"]", "[\"VIP\",\"New\"]").Should().BeFalse();

    [Theory]
    [InlineData("EqualValues", "equal-values")]
    [InlineData("FirstLetter", "first-letter")]
    [InlineData("Increment1000", "1000")]
    [InlineData("Increment1", "1")]
    [InlineData("Month", "month")]
    [InlineData(null, "equal-values")]
    public void Grouping_UsesQuickbaseValues(string? ours, string qb) => QbReportService.Grouping(ours).Should().Be(qb);

    [Fact]
    public void SafeParse_RejectsDtd()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaa\">]><qdbapi><udata>&a;</udata></qdbapi>";
        var act = () => QbXmlRequest.SafeParse(xml);
        act.Should().Throw<System.Xml.XmlException>();
        QbXmlTokenMiddleware.ReadToken("<!DOCTYPE x [<!ENTITY t \"pb_ut_x\">]><qdbapi><usertoken>&t;</usertoken></qdbapi>").Should().BeNull();
    }

    [Fact]
    public void XmlResponse_DropsInvalidCharacters_KeepsEmoji()
    {
        var xml = new QbXmlResponse("API_GetUserInfo").ToXml("a\u0001b😀");
        xml.Should().Contain("<udata>ab😀</udata>");
    }

    [Theory]
    [InlineData("10:50:00.0000000", "10:50:00")]
    [InlineData("07:05", "07:05:00")]
    public void TimeOfDay_IsHhMmSs(string stored, string qb) => QbValueFormatter.Format(stored, "timeofday").Should().Be(qb);

    [Fact]
    public void TimeOfDay_FromTimeOnly() => QbValueFormatter.Format(new TimeOnly(9, 4, 3), "timeofday").Should().Be("09:04:03");

    [Fact]
    public void ParseGuids_ReadsOneOrMany()
    {
        var g = Guid.NewGuid();
        QbUserValueResolver.ParseGuids(g.ToString()).Should().Equal(g);
        QbUserValueResolver.ParseGuids($"[\"{g}\"]").Should().Equal(g);
        QbUserValueResolver.ParseGuids(null).Should().BeEmpty();
    }
}
