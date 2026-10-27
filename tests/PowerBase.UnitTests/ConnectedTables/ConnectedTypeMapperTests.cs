using FluentAssertions;
using PowerBase.Application.ConnectedTables.Mapping;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.ConnectedTables;

public class ConnectedTypeMapperTests
{
    private static AppField Field(string type, int fid = 10, bool system = false, string? settings = null) =>
        new() { Id = fid, Fid = fid, TypeCode = type, Name = "f", Label = "F", IsSystem = system, Settings = settings };

    [Theory]
    [InlineData("Text")]
    [InlineData("Number")]
    [InlineData("Currency")]
    [InlineData("Date")]
    [InlineData("DateTime")]
    [InlineData("Boolean")]
    [InlineData("SingleSelect")]
    [InlineData("MultiSelect")]
    [InlineData("Address")]
    [InlineData("DateRange")]
    public void Plan_PlainType_KeepsTypeAndSettings(string type)
    {
        var plan = ConnectedTypeMapper.Plan(Field(type, settings: "{\"x\":1}"), crossTenant: true);

        plan.Connectable.Should().BeTrue();
        plan.DestinationTypeCode.Should().Be(type);
        plan.Conversion.Should().Be(ConnectedConversions.Identity);
        plan.Settings.Should().Be("{\"x\":1}");
    }

    [Theory]
    [InlineData("File")]
    [InlineData("ReportLink")]
    [InlineData("ActionButton")]
    [InlineData("ActionButton_Data")]
    public void Plan_NotConnectableType_ReturnsReason(string type)
    {
        var plan = ConnectedTypeMapper.Plan(Field(type), crossTenant: false);

        plan.Connectable.Should().BeFalse();
        plan.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Plan_UserSameRealm_StaysUser()
    {
        var plan = ConnectedTypeMapper.Plan(Field("User"), crossTenant: false);
        plan.DestinationTypeCode.Should().Be("User");
        plan.Conversion.Should().Be(ConnectedConversions.Identity);
    }

    [Fact]
    public void Plan_UserAcrossRealms_BecomesEmailText()
    {
        ConnectedTypeMapper.Plan(Field("User"), crossTenant: true).Should()
            .BeEquivalentTo(new { Connectable = true, DestinationTypeCode = "Email", Conversion = ConnectedConversions.UserToEmail });
        ConnectedTypeMapper.Plan(Field("MultiUser"), crossTenant: true).DestinationTypeCode.Should().Be("TextMultiLine");
    }

    [Fact]
    public void Plan_Reference_BecomesNumber()
    {
        var plan = ConnectedTypeMapper.Plan(Field("Reference"), crossTenant: false);
        plan.DestinationTypeCode.Should().Be("Number");
        plan.Conversion.Should().Be(ConnectedConversions.ReferenceToNumber);
    }

    [Theory]
    [InlineData("Formula_Number", "Number")]
    [InlineData("Formula_Date", "Date")]
    [InlineData("Formula_Bool", "Boolean")]
    [InlineData("Formula_Url", "Url")]
    public void Plan_FormulaVariant_BecomesResultType(string type, string expected)
    {
        var plan = ConnectedTypeMapper.Plan(Field(type), crossTenant: false);
        plan.DestinationTypeCode.Should().Be(expected);
        plan.Conversion.Should().Be(ConnectedConversions.Computed);
    }

    [Fact]
    public void Plan_GenericFormula_UsesResultTypeSetting()
    {
        ConnectedTypeMapper.Plan(Field("Formula", settings: "{\"resultType\":\"Number\"}"), false).DestinationTypeCode.Should().Be("Number");
        ConnectedTypeMapper.Plan(Field("Formula"), false).DestinationTypeCode.Should().Be("Text");
    }

    [Fact]
    public void Plan_Summary_UsesFunction()
    {
        ConnectedTypeMapper.Plan(Field("Summary", settings: "{\"function\":\"Sum\"}"), false).DestinationTypeCode.Should().Be("Number");
        ConnectedTypeMapper.Plan(Field("Summary", settings: "{\"function\":\"CombinedText\"}"), false).DestinationTypeCode.Should().Be("Text");
        ConnectedTypeMapper.Plan(Field("Summary", settings: "{\"function\":\"Exists\"}"), false).DestinationTypeCode.Should().Be("Boolean");
    }

    [Fact]
    public void Plan_Lookup_UsesSourceType()
    {
        ConnectedTypeMapper.Plan(Field("Lookup", settings: "{\"sourceTypeCode\":\"Date\"}"), false).DestinationTypeCode.Should().Be("Date");
        ConnectedTypeMapper.Plan(Field("Lookup", settings: "{\"sourceTypeCode\":\"MultiSelect\"}"), false).DestinationTypeCode.Should().Be("Text");
    }

    [Theory]
    [InlineData(3, "Number")]
    [InlineData(1, "DateTime")]
    [InlineData(2, "DateTime")]
    public void Plan_BuiltInFields_AreConnectable(int fid, string expected)
    {
        ConnectedTypeMapper.Plan(Field("Number", fid, system: true), false).DestinationTypeCode.Should().Be(expected);
    }

    [Fact]
    public void IsCompatible_SameFamily_IsTrue_OtherFamily_IsFalse()
    {
        var plan = ConnectedTypeMapper.Plan(Field("Text"), false);
        ConnectedTypeMapper.IsCompatible(plan, "Text").Should().BeTrue();
        ConnectedTypeMapper.IsCompatible(plan, "TextMultiLine").Should().BeTrue();
        ConnectedTypeMapper.IsCompatible(plan, "Number").Should().BeFalse();
    }
}
