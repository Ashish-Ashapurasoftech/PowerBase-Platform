using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Shapes measured on a live Quickbase realm with a table holding every field type.</summary>
public class QbAllTypesLiveTests
{
    [Theory]
    [InlineData("{\"street1\":\"1 Main Street\",\"city\":\"Boston\",\"zip\":\"02110\"}", "1 Main Street, Boston 02110")]
    [InlineData("{\"street1\":\"10 main street\",\"city\":\"Cambridge\",\"state\":\"MA\",\"zip\":\"02139\",\"country\":\"USA\"}", "10 main street, Cambridge, MA 02139")]
    public void Address_IsWrittenLikeQuickbase(string stored, string expected) =>
        QbValueFormatter.Format(stored, "address").Should().Be(expected);

    [Fact]
    public void MultiLineText_IsItsOwnType_WithSixLines()
    {
        var f = new AppField { TypeCode = "TextMultiLine", Fid = 7 };
        var t = QbFieldPropertiesBuilder.TypeOf(f);
        t.FieldType.Should().Be("text-multi-line");
        var p = QbFieldShape.Properties(f, t);
        p["numLines"].Should().Be(6);
        p["width"].Should().Be(60);
        QbFieldShape.XmlField(f, t).Attribute("field_type")!.Value.Should().Be("text");
    }

    [Fact]
    public void RichText_InXml_IsText_WithWidth()
    {
        var f = new AppField { TypeCode = "RichText", Fid = 8 };
        var el = QbFieldShape.XmlField(f, QbFieldPropertiesBuilder.TypeOf(f));
        el.Attribute("field_type")!.Value.Should().Be("text");
        el.Element("width")!.Value.Should().Be("60");
        el.Element("num_lines")!.Value.Should().Be("6");
    }

    [Fact]
    public void RecordId_InXml_IsInt32Virtual()
    {
        var f = new AppField { TypeCode = "Number", Fid = 3, IsSystem = true };
        var el = QbFieldShape.XmlField(f, QbFieldPropertiesBuilder.TypeOf(f));
        el.Attribute("base_type")!.Value.Should().Be("int32");
        el.Attribute("mode")!.Value.Should().Be("virtual");
    }

    [Fact]
    public void Reference_HasMasterTable_AndQuickbaseFlags()
    {
        var f = new AppField { TypeCode = "Reference", Fid = 9, IsSearchable = true, IsAuditable = true };
        var t = QbFieldPropertiesBuilder.TypeOf(f);
        var ctx = new QbFieldContext(MasterTableId: "abc", MasterTableTag: "_DBID_PARENT", MasterKeyFid: 3);
        var p = QbFieldShape.Properties(f, t, ctx);
        p["masterTableTag"].Should().Be("_DBID_PARENT");
        p["masterTableKeyFid"].Should().Be(3);
        p["foreignKey"].Should().Be(true);
        QbFieldShape.FindEnabled(f, t).Should().BeFalse();
        QbFieldShape.AppearsByDefault(f).Should().BeFalse();
        QbFieldShape.Audited(f, t).Should().BeFalse();
        var el = QbFieldShape.XmlField(f, t, ctx);
        el.Element("mastag")!.Value.Should().Be("_DBID_PARENT");
        el.Element("foreignkey")!.Value.Should().Be("0");
        el.Attribute("base_type")!.Value.Should().Be("float");
    }

    [Fact]
    public void Lookup_InheritsDecimals_AndHasLutfidLusfid()
    {
        var lookup = new AppField { TypeCode = "Lookup", Fid = 12, Settings = "{\"resultTypeCode\":\"Number\",\"sourceFid\":11,\"referenceFid\":9}" };
        var source = new AppField { TypeCode = "Number", Fid = 11, Settings = "{\"decimals\":2}" };
        var t = QbFieldPropertiesBuilder.TypeOf(lookup);
        var p = QbFieldShape.Properties(lookup, t, new QbFieldContext(Inherit: source));
        p["decimalPlaces"].Should().Be(2);
        p["displayAsLink"].Should().Be(false);
        var el = QbFieldShape.XmlField(lookup, t, new QbFieldContext(Inherit: source));
        el.Element("lutfid")!.Value.Should().Be("11");
        el.Element("lusfid")!.Value.Should().Be("9");
    }

    [Fact]
    public void Currency_DefaultsToTwoDecimals() =>
        QbFieldShape.Properties(new AppField { TypeCode = "Currency", Fid = 20 }, new QbFieldType("currency", "", "float"))["decimalPlaces"].Should().Be(2);

    [Fact]
    public void TableTag_FromName() =>
        QbReferenceTypes.TableTag(new AppTable { Name = "PF All Types" }).Should().Be("_DBID_PF_ALL_TYPES");
}
