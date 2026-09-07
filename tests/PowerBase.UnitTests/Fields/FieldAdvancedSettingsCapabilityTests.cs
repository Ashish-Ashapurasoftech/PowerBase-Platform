using FluentAssertions;
using PowerBase.Application.Fields;

namespace PowerBase.UnitTests.Fields;

public class FieldAdvancedSettingsCapabilityTests
{
    [Theory]
    [InlineData("Text", true, true, true, true, false)]
    [InlineData("TextMultiLine", false, false, true, false, false)]
    [InlineData("RichText", true, false, true, true, false)]
    [InlineData("Number", false, true, true, false, false)]
    [InlineData("NumericRange", false, false, true, false, false)]
    [InlineData("Boolean", true, true, true, true, false)]
    [InlineData("Url", false, false, true, false, false)]
    [InlineData("File", false, false, true, false, false)]
    [InlineData("Address", true, false, true, true, false)]
    [InlineData("User", true, true, true, true, false)]
    [InlineData("MultiUser", true, false, true, true, false)]
    [InlineData("ReportLink", false, false, true, false, false)]
    [InlineData("ActionButton_File", false, false, true, false, false)]
    public void Resolve_KnownTypes_MatchesMatrix(
        string typeCode, bool searchable, bool sortable, bool reportable, bool filterable, bool auditable)
    {
        var defaults = FieldAdvancedSettingsCapability.Resolve(typeCode, settingsJson: null);

        defaults.Should().Be(new FieldAdvancedSettingsCapability.Defaults(searchable, sortable, reportable, filterable, auditable));
    }

    [Theory]
    [InlineData("Formula_Text", true, true, true, true, false)]
    [InlineData("Formula_Number", false, true, true, false, false)]
    [InlineData("Formula_Bool", true, true, true, true, false)]
    [InlineData("Formula_Url", false, false, true, false, false)]
    public void Resolve_FormulaTypedVariants_MatchesMatrix(
        string typeCode, bool searchable, bool sortable, bool reportable, bool filterable, bool auditable)
    {
        var defaults = FieldAdvancedSettingsCapability.Resolve(typeCode, settingsJson: null);

        defaults.Should().Be(new FieldAdvancedSettingsCapability.Defaults(searchable, sortable, reportable, filterable, auditable));
    }

    [Fact]
    public void Resolve_GenericFormulaTypeCodeWithResultTypeSetting_MatchesVariant()
    {
        var defaults = FieldAdvancedSettingsCapability.Resolve("Formula", "{\"resultType\":\"Number\"}");

        defaults.Should().Be(new FieldAdvancedSettingsCapability.Defaults(false, true, true, false, false));
    }

    [Theory]
    [InlineData("Reference")]
    [InlineData("Lookup")]
    [InlineData("Summary")]
    [InlineData("SomethingUnrecognized")]
    public void Resolve_TypesOutsideTheMatrix_ReturnsNull(string typeCode)
    {
        FieldAdvancedSettingsCapability.Resolve(typeCode, settingsJson: null).Should().BeNull();
    }

    [Fact]
    public void Resolve_AuditableDefaultIsAlwaysFalse()
    {
        // Every type in the matrix defaults Auditable to false — it's opt-in only, regardless of type.
        foreach (var typeCode in new[]
        {
            "Text", "SingleSelect", "Number", "Date", "Boolean", "Email", "User",
            "Formula_Text", "Formula_Bool", "Formula_User",
        })
        {
            FieldAdvancedSettingsCapability.Resolve(typeCode, settingsJson: null)!.Value.Auditable.Should().BeFalse();
        }
    }
}
