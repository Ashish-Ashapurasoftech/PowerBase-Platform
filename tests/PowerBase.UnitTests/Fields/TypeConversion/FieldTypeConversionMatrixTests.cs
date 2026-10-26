using FluentAssertions;
using PowerBase.Application.Fields.TypeConversion;

namespace PowerBase.UnitTests.Fields.TypeConversion;

public class FieldTypeConversionMatrixTests
{
    [Theory]
    [InlineData("MultiSelect")]
    [InlineData("Address")]
    [InlineData("MultiUser")]
    [InlineData("Formula_ListUser")]
    [InlineData("Formula_MultiSelect")]
    [InlineData("ActionButton_File")]
    [InlineData("ActionButton_Signature")]
    [InlineData("ActionButton_Prompt")]
    [InlineData("ActionButton_Data")]
    [InlineData("Reference")]
    [InlineData("Lookup")]
    [InlineData("Summary")]
    [InlineData("ReportLink")]
    [InlineData("DateRange")]
    [InlineData("NumericRange")]
    public void Check_UnsupportedType_IsBlockedBothWays(string unsupported)
    {
        FieldTypeConversionMatrix.Check(unsupported, "Text").IsAllowed.Should().BeFalse();
        FieldTypeConversionMatrix.Check("Text", unsupported).IsAllowed.Should().BeFalse();
        FieldTypeConversionMatrix.GetAllowedTargets(unsupported).Should().BeEmpty();
        FieldTypeConversionMatrix.GetAllowedTargets("Text").Select(t => t.TargetTypeCode).Should().NotContain(unsupported);
    }

    [Fact]
    public void Check_SameType_IsBlocked() =>
        FieldTypeConversionMatrix.Check("Text", "Text").IsAllowed.Should().BeFalse();

    [Fact]
    public void Check_SystemField_IsBlocked() =>
        FieldTypeConversionMatrix.GetAllowedTargets("DateTime", new FieldConversionConstraints(IsSystemField: true)).Should().BeEmpty();

    [Fact]
    public void GetAllowedTargets_RelationshipKey_OnlyKeyEligibleTypes()
    {
        var targets = FieldTypeConversionMatrix.GetAllowedTargets("Text", new FieldConversionConstraints(IsRelationshipKey: true))
            .Select(t => t.TargetTypeCode);

        targets.Should().BeEquivalentTo(["Number", "Currency", "Percent", "Rating", "Date"]);
    }

    [Theory]
    [InlineData("Number", "Text", ConversionKind.ScalarToScalar, DataLossLevel.None)]
    [InlineData("Text", "Number", ConversionKind.ScalarToScalar, DataLossLevel.Possible)]
    [InlineData("Text", "Boolean", ConversionKind.ScalarToScalar, DataLossLevel.Possible)]
    [InlineData("Date", "DateTime", ConversionKind.ScalarToScalar, DataLossLevel.None)]
    [InlineData("DateTime", "Date", ConversionKind.ScalarToScalar, DataLossLevel.Possible)]
    [InlineData("Number", "Currency", ConversionKind.NumericFamily, DataLossLevel.None)]
    [InlineData("Currency", "Percent", ConversionKind.NumericFamily, DataLossLevel.None)]
    [InlineData("Text", "File", ConversionKind.ScalarToScalar, DataLossLevel.Destructive)]
    [InlineData("Formula_Text", "Text", ConversionKind.FormulaToScalar, DataLossLevel.None)]
    [InlineData("Formula_Text", "Number", ConversionKind.FormulaToScalar, DataLossLevel.Possible)]
    [InlineData("Formula_Number", "Text", ConversionKind.FormulaToScalar, DataLossLevel.None)]
    [InlineData("Text", "Formula_Text", ConversionKind.ScalarToFormula, DataLossLevel.Destructive)]
    [InlineData("Formula_Text", "Formula_Number", ConversionKind.FormulaToFormula, DataLossLevel.None)]
    public void Check_AllowedPairs_HaveExpectedKindAndLoss(string source, string target, ConversionKind kind, DataLossLevel loss)
    {
        var check = FieldTypeConversionMatrix.Check(source, target);

        check.IsAllowed.Should().BeTrue();
        check.Kind.Should().Be(kind);
        check.Loss.Should().Be(loss);
    }

    [Fact]
    public void GetAllowedTargets_File_OnlyTextAndFormulas()
    {
        var targets = FieldTypeConversionMatrix.GetAllowedTargets("File").Select(t => t.TargetTypeCode).ToList();

        targets.Should().Contain(["Text", "TextMultiLine"]);
        targets.Where(t => !FieldTypeConversionMatrix.IsFormula(t)).Should().BeEquivalentTo(["Text", "TextMultiLine"]);
    }

    [Fact]
    public void Check_FormulaToFile_IsBlocked() =>
        FieldTypeConversionMatrix.Check("Formula_Text", "File").IsAllowed.Should().BeFalse();

    [Fact]
    public void GetAllowedTargets_Text_OffersEverySupportedTypeExceptItself()
    {
        var targets = FieldTypeConversionMatrix.GetAllowedTargets("Text").Select(t => t.TargetTypeCode);

        targets.Should().BeEquivalentTo(FieldTypeConversionMatrix.AllSupportedTypes.Where(t => t != "Text"));
    }

    [Theory]
    [InlineData("Formula_Text", "Text")]
    [InlineData("Formula_Bool", "Boolean")]
    [InlineData("Formula_User", "User")]
    [InlineData("Text", "Text")]
    public void ValueTypeOf_FormulaUsesScalarTwin(string type, string expected) =>
        FieldTypeConversionMatrix.ValueTypeOf(type).Should().Be(expected);
}
