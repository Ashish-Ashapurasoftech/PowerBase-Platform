using System.Text.Json.Nodes;
using FluentAssertions;
using PowerBase.Application.Fields.TypeConversion;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Fields.TypeConversion;

public class FieldTypeChangeSettingsTests
{
    private static AppField Field(string type, string? settings = null, string? defaultValue = null) =>
        new() { Id = 1, Fid = 6, Name = "f", Label = "Field", TypeCode = type, Settings = settings, DefaultValue = defaultValue };

    [Fact]
    public void Build_FormulaToFormula_KeepsExpressionOnly()
    {
        var settings = FieldTypeChangeSettings.Build(
            Field("Formula_Text", "{\"expression\":\"[fid_6|Name] & \\\" - \\\"\",\"displayBold\":true}"), "Formula_Number", ConversionKind.FormulaToFormula);

        var obj = JsonNode.Parse(settings!)!.AsObject();
        obj["expression"]!.GetValue<string>().Should().Be("[fid_6|Name] & \" - \"");
        obj.ContainsKey("displayBold").Should().BeFalse();
    }

    [Fact]
    public void Build_FormulaToFormulaUrl_UsesTemplate() =>
        FieldTypeChangeSettings.Build(Field("Formula_Text", "{\"expression\":\"\\\"x\\\"\"}"), "Formula_Url", ConversionKind.FormulaToFormula)
            .Should().Be("{\"template\":\"\\u0022x\\u0022\"}");

    [Fact]
    public void Build_ScalarToFormula_StartsEmptyNeverRestoresOldExpression() =>
        FieldTypeChangeSettings.Build(Field("Text", "{\"expression\":\"old\"}"), "Formula_Text", ConversionKind.ScalarToFormula).Should().BeNull();

    [Fact]
    public void Build_NumericFamily_DisplayAsFollowsNewTypeAndKeepsOthers()
    {
        var settings = FieldTypeChangeSettings.Build(Field("Number", "{\"decimals\":2,\"displayAs\":\"Number\"}"), "Currency", ConversionKind.NumericFamily);

        var obj = JsonNode.Parse(settings!)!.AsObject();
        obj["displayAs"]!.GetValue<string>().Should().Be("Currency");
        obj["decimals"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Build_SingleSelect_ChoicesAsOptions() =>
        FieldTypeChangeSettings.Build(Field("Text"), "SingleSelect", ConversionKind.ScalarToScalar, ["a", "b"])
            .Should().Be("{\"options\":[\"a\",\"b\"]}");

    [Fact]
    public void Build_OtherScalar_DefaultSettings() =>
        FieldTypeChangeSettings.Build(Field("Text", "{\"displayBold\":true}"), "Date", ConversionKind.ScalarToScalar).Should().BeNull();

    [Theory]
    [InlineData("Text", "abc12", "Number", "12")]
    [InlineData("Number", "12.5", "Text", "12.5")]
    [InlineData("Text", "x", "Number", null)]
    [InlineData("Text", "10-02-2026", "Date", null)]      // non text/numeric defaults are cleared
    [InlineData("Text", "abc", "Formula_Text", null)]
    public void BuildDefaultValue_ConvertsTextAndNumericOnly(string from, string dflt, string to, string? expected) =>
        FieldTypeChangeSettings.BuildDefaultValue(Field(from, defaultValue: dflt), to, FieldValueConversionContext.Default).Should().Be(expected);
}
