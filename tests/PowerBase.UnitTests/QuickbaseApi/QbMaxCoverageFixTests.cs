using System.Text.Json;
using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Fixes found by the 5-table "QB Max Coverage" run: range fields, address objects, formula text, action buttons.</summary>
public class QbMaxCoverageFixTests
{
    private static JsonElement J(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

    [Fact]
    public void Format_NumericRange_ReturnsStartEndNumbers()
    {
        var v = (IDictionary<string, object?>)QbValueFormatter.Format(J("{\"start\":10.0000,\"end\":20.5000}"), "numeric")!;
        v["start"].Should().Be(10L);
        v["end"].Should().Be(20.5m);
    }

    [Fact]
    public void Format_DateRange_ReturnsIsoDates()
    {
        var v = (IDictionary<string, object?>)QbValueFormatter.Format("{\"start\":\"2026-01-01T00:00:00\",\"end\":\"2026-01-10T00:00:00\"}", "date")!;
        v["start"].Should().Be("2026-01-01");
        v["end"].Should().Be("2026-01-10");
    }

    [Theory]
    [InlineData("\"10 - 20\"")]
    [InlineData("\"10 to 20\"")]
    [InlineData("{\"start\":10,\"end\":20}")]
    public void RangeValue_NumericInputs(string json)
    {
        var field = new AppField { TypeCode = "NumericRange", Fid = 13 };
        var v = (JsonElement)QbRecordService.RangeValue(J(json), field)!;
        v.GetProperty("start").GetDecimal().Should().Be(10m);
        v.GetProperty("end").GetDecimal().Should().Be(20m);
    }

    [Fact]
    public void RangeValue_DateText()
    {
        var v = (JsonElement)QbRecordService.RangeValue(J("\"2026-01-01 to 2026-03-31\""), new AppField { TypeCode = "DateRange", Fid = 15 })!;
        v.GetProperty("start").GetString().Should().Be("2026-01-01");
        v.GetProperty("end").GetString().Should().Be("2026-03-31");
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"x - y\"")]
    [InlineData("5")]
    public void RangeValue_Invalid_IsBadRequestNotCrash(string json)
    {
        var act = () => QbRecordService.RangeValue(J(json), new AppField { TypeCode = "NumericRange", Fid = 13 });
        act.Should().Throw<QbApiException>();
    }

    [Fact]
    public void QbFormula_StripsInternalFieldIds()
    {
        QbFieldPropertiesBuilder.QbFormula("[fid_7|Name] & \" (\" & [fid_6|Code] & \")\"").Should().Be("[Name] & \" (\" & [Code] & \")\"");
        QbFieldPropertiesBuilder.QbFormula(null).Should().BeNull();
    }

    [Fact]
    public void FormulaUrl_TemplateIsReportedAsFormula()
    {
        var p = QbFieldPropertiesBuilder.BuildProperties(new AppField { TypeCode = "Formula_Url", Settings = "{\"template\":\"\\\"https://x/\\\" & [fid_6|Code]\"}" });
        p["formula"].Should().Be("\"https://x/\" & [Code]");
    }

    [Fact]
    public void ActionButton_MapsToFormulaUrl()
    {
        QbFieldTypeMap.Map("ActionButton_Data").Should().Be(new QbFieldType("url", "formula", "text"));
        QbFieldTypeMap.Map("ActionButton").FieldType.Should().Be("url");
    }
}
