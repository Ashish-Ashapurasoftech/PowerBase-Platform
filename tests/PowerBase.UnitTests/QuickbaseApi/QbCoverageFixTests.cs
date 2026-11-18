using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.API.QuickbaseApi.Xml;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbCoverageFixTests
{
    [Fact]
    public void NoWrapAndBold_AbsentSettingIsFalse()
    {
        var f = new AppField { TypeCode = "Text", Fid = 6, Settings = null };
        QbFieldPropertiesBuilder.NoWrap(f).Should().BeFalse();
        QbFieldPropertiesBuilder.Bold(f).Should().BeFalse();
        QbFieldPropertiesBuilder.Bold(new AppField { TypeCode = "Text", Settings = "{\"displayBold\":true}" }).Should().BeTrue();
    }

    [Fact]
    public void Currency_TypeCode_MapsSymbolAndPosition()
    {
        var p = QbFieldPropertiesBuilder.BuildProperties(new AppField { TypeCode = "Currency", Settings = "{\"symbol\":\"$\",\"position\":\"after\"}" });
        p["currencySymbol"].Should().Be("$");
        p["currencyFormat"].Should().Be("right");
    }

    [Fact]
    public void TypeOf_OverrideWins_ForReferenceFields()
    {
        var reference = new AppField { Id = 77, TypeCode = "Reference", Fid = 12 };
        QbFieldPropertiesBuilder.TypeOf(reference).FieldType.Should().Be("numeric");
        QbFieldPropertiesBuilder.TypeOf(reference, new Dictionary<long, QbFieldType> { [77] = new("text", "", "text") }).FieldType.Should().Be("text");
    }

    [Fact]
    public void FileVersion_UploadedHasNoFraction()
    {
        var v = (IDictionary<string, object?>)QbValueFormatter.Format(
            "{\"name\":\"a.txt\",\"path\":\"/files/a.txt\",\"uploadedOn\":\"2026-11-05T07:12:01.000003Z\",\"uploadedBy\":\"Jo\"}", "file", "/apps/x/files/download")!;
        var version = ((IEnumerable<object>)v["versions"]!).Cast<IDictionary<string, object?>>().Single();
        version["uploaded"].Should().Be("2026-11-05T07:12:01Z");
    }

    [Fact]
    public void FieldElement_Summary_HasReferenceAndTargetFid()
    {
        var el = QbXmlRecordActions.FieldElement(new AppField { Fid = 32, TypeCode = "Summary", Label = "Order Count",
            Settings = "{\"function\":\"Sum\",\"targetFid\":7,\"referenceFid\":12,\"resultTypeCode\":\"Number\"}" });
        el.Attribute("mode")!.Value.Should().Be("summary");
        el.Element("summaryReferenceFid")!.Value.Should().Be("12");
        el.Element("summaryTargetFid")!.Value.Should().Be("7");
        el.Element("nowrap")!.Value.Should().Be("1");            // numbers stay on one line in Quickbase
        el.Element("summaryFunction")!.Value.Should().Be("Total");
    }
}
