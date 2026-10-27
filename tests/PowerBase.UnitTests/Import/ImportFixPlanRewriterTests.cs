using FluentAssertions;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>C1 of docs/QBL_REIMPORT_FIX_PLAN.md: the formulas that used to need manual review.</summary>
public class ImportFixPlanRewriterTests
{
    private readonly FormulaTranslator _sut = new(new FormulaEngine());

    private static List<AppField> Fields() =>
    [
        new() { Name = "Qty", TypeCode = "Number", Fid = 6 },
        new() { Name = "Name", TypeCode = "Text", Fid = 7 },
        new() { Name = "Date Of Birth", TypeCode = "Date", Fid = 8 },
        new() { Name = "Latest OLP Service", TypeCode = "Date", Fid = 9 },
        new() { Name = "Latest T Service", TypeCode = "Date", Fid = 10 },
        new() { Name = "Clicked A", TypeCode = "DateTime", Fid = 11 },
        new() { Name = "Clicked B", TypeCode = "DateTime", Fid = 12 },
        new() { Name = "Clicked C", TypeCode = "DateTime", Fid = 13 },
        new() { Name = "Open", TypeCode = "Number", Fid = 14 },
        new() { Name = "Status", TypeCode = "Text", Fid = 15 },
    ];

    private FormulaTranslationResult Translate(string type, string expression)
    {
        var result = _sut.Translate(type, expression, Fields());
        result.Status.Should().NotBe(FormulaTranslationStatus.NeedsManualReview, string.Join("; ", result.Diagnostics));
        return result;
    }

    [Fact]
    public void ConcatInsideAVarFormula_IsFixed()
    {
        var result = Translate("Text", """
            var text a = [Name] & [Qty];
            $a & [Qty]
            """);

        result.SettingsJson.Should().Contain("ToText");
    }

    [Fact]
    public void MultiBranchIfInsideAVarDeclaration_IsNested()
    {
        Translate("Number", """
            var date d = [Date Of Birth];
            var number m = If(
              Month(Today()) > Month($d), 1,
              Month(Today()) = Month($d) and Day(Today()) >= Day($d), 2,
              Month(Today()) = Month($d) and Day(Today()) < Day($d), 3,
              4);
            $m
            """);
    }

    [Fact]
    public void MaxOfDates_BecomesAComparison()
    {
        var result = Translate("Date", """
            var Date later = Max([Latest OLP Service],[Latest T Service]);
            $later+Days(5)
            """);

        result.SettingsJson.Should().Contain("If(");
    }

    [Fact]
    public void MinOfThreeDateTimes_BecomesNestedComparisons()
    {
        var result = Translate("Text", """
            var datetime first = Min([Clicked A],[Clicked B],[Clicked C]);
            If(Now()>$first+Minutes(5),"a","b")
            """);

        result.SettingsJson.Should().Contain("If(");
    }

    [Fact]
    public void TextComparedWithANumberByGreaterThan_ReadsTheTextAsANumber()
    {
        var result = Translate("Bool", "[Status] > 0");

        result.SettingsJson.Should().Contain("ToNumber([Status])");
    }

    [Fact]
    public void TextEqualToNumberInsideVar_IsComparedAsText()
    {
        var result = Translate("Text", """
            var bool x = [Status] = 1;
            If($x, "a", "b")
            """);

        result.SettingsJson.Should().Contain("ToText(1)");
    }

    [Fact]
    public void VarDeclaredInsideAnIfBranch_IsMovedToTheTop()
    {
        var result = _sut.Translate("Text", """
            If([Open]>0,"stop",
            var text one = "a" & [Status];
            var text two = "b";
            $one & $two)
            """, Fields());

        result.Status.Should().Be(FormulaTranslationStatus.Adjusted, string.Join("; ", result.Diagnostics));
        result.SettingsJson!.IndexOf("var text one", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0)
            .And.BeLessThan(result.SettingsJson.IndexOf("If(", StringComparison.Ordinal));
    }
}
