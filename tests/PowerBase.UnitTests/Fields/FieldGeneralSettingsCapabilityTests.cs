using FluentAssertions;
using PowerBase.Application.Fields;

namespace PowerBase.UnitTests.Fields;

public class FieldGeneralSettingsCapabilityTests
{
    [Theory]
    [InlineData("Text", true, true, true)]
    [InlineData("TextMultiLine", true, false, true)]
    [InlineData("NumericRange", true, false, true)]
    [InlineData("File", true, false, false)]
    [InlineData("Address", false, false, false)]
    [InlineData("User", true, true, true)]
    [InlineData("MultiUser", true, false, true)]
    public void Resolve_KnownTypes_MatchesMatrix(string typeCode, bool required, bool unique, bool @default)
    {
        var cap = FieldGeneralSettingsCapability.Resolve(typeCode, settingsJson: null);

        cap.Should().NotBeNull();
        cap!.Value.Required.Should().Be(required);
        cap.Value.Unique.Should().Be(unique);
        cap.Value.Default.Should().Be(@default);
    }

    [Theory]
    [InlineData("Formula_Text")]
    [InlineData("Formula_Number")]
    [InlineData("Formula_Duration")]
    public void Resolve_FormulaScalarResultTypes_UniqueOnly(string typeCode)
    {
        var cap = FieldGeneralSettingsCapability.Resolve(typeCode, settingsJson: null);

        cap.Should().Be(new FieldGeneralSettingsCapability.Capabilities(false, true, false));
    }

    [Theory]
    [InlineData("Formula_Bool")]
    [InlineData("Formula_User")]
    public void Resolve_FormulaBoolOrUserResultTypes_NoneAllowed(string typeCode)
    {
        var cap = FieldGeneralSettingsCapability.Resolve(typeCode, settingsJson: null);

        cap.Should().Be(new FieldGeneralSettingsCapability.Capabilities(false, false, false));
    }

    [Fact]
    public void Resolve_GenericFormulaTypeCodeWithResultTypeSetting_MatchesVariant()
    {
        var cap = FieldGeneralSettingsCapability.Resolve("Formula", "{\"resultType\":\"Number\"}");

        cap.Should().Be(new FieldGeneralSettingsCapability.Capabilities(false, true, false));
    }

    [Theory]
    [InlineData("Reference")]
    [InlineData("Lookup")]
    [InlineData("Summary")]
    [InlineData("ReportLink")]
    [InlineData("ActionButton")]
    [InlineData("SomethingUnrecognized")]
    public void Resolve_TypesOutsideTheMatrix_ReturnsNull(string typeCode)
    {
        FieldGeneralSettingsCapability.Resolve(typeCode, settingsJson: null).Should().BeNull();
    }

    [Fact]
    public void Validate_RequiredOnUnsupportedType_ReturnsError()
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "Address", settingsJson: null, label: "Home Address",
            newIsRequired: true, newIsUnique: null, newDefaultValue: null);

        errors.Should().ContainKey("IsRequired");
    }

    [Fact]
    public void Validate_RequiredOff_NeverRejectedEvenWhenUnsupported()
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "Address", settingsJson: null, label: "Home Address",
            newIsRequired: false, newIsUnique: false, newDefaultValue: null);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_UniqueOnUnsupportedType_ReturnsError()
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "NumericRange", settingsJson: null, label: "Budget",
            newIsRequired: false, newIsUnique: true, newDefaultValue: null);

        errors.Should().ContainKey("IsUnique");
    }

    [Fact]
    public void Validate_DefaultOnUnsupportedType_ReturnsError()
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "File", settingsJson: null, label: "Attachment",
            newIsRequired: false, newIsUnique: null, newDefaultValue: "some-file.pdf");

        errors.Should().ContainKey("DefaultValue");
    }

    [Fact]
    public void Validate_TypeOutsideMatrix_NeverRejectsAnything()
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "ActionButton", settingsJson: null, label: "Approve",
            newIsRequired: true, newIsUnique: true, newDefaultValue: "x");

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("yes", false)]
    public void Validate_BooleanDefaultShape(string value, bool expectedValid)
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "Boolean", settingsJson: null, label: "Active",
            newIsRequired: false, newIsUnique: null, newDefaultValue: value);

        errors.ContainsKey("DefaultValue").Should().Be(!expectedValid);
    }

    [Theory]
    [InlineData("{\"start\":\"1\",\"end\":\"10\"}", true)]
    [InlineData("not-json", false)]
    [InlineData("[1,10]", false)]
    public void Validate_RangeDefaultShape(string value, bool expectedValid)
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "NumericRange", settingsJson: null, label: "Budget",
            newIsRequired: false, newIsUnique: null, newDefaultValue: value);

        errors.ContainsKey("DefaultValue").Should().Be(!expectedValid);
    }

    [Theory]
    [InlineData("{\"mode\":\"None\"}", true)]
    [InlineData("{\"mode\":\"CurrentUser\"}", true)]
    [InlineData("{\"mode\":\"SpecificUser\",\"userPublicId\":\"11111111-1111-1111-1111-111111111111\"}", true)]
    [InlineData("{\"mode\":\"SpecificUser\"}", false)]
    [InlineData("{\"mode\":\"Bogus\"}", false)]
    public void Validate_UserDefaultShape(string value, bool expectedValid)
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "User", settingsJson: null, label: "Owner",
            newIsRequired: false, newIsUnique: null, newDefaultValue: value);

        errors.ContainsKey("DefaultValue").Should().Be(!expectedValid);
    }

    [Fact]
    public void Validate_MultiUserDefaultShape_RejectsSpecificUserMode()
    {
        var errors = FieldGeneralSettingsCapability.Validate(
            "MultiUser", settingsJson: null, label: "Watchers",
            newIsRequired: false, newIsUnique: null,
            newDefaultValue: "{\"mode\":\"SpecificUser\",\"userPublicId\":\"11111111-1111-1111-1111-111111111111\"}");

        errors.Should().ContainKey("DefaultValue");
    }
}
