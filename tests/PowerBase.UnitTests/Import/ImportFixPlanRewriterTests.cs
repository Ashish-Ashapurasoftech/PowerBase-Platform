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
        new() { Name = "Done", TypeCode = "Boolean", Fid = 16 },
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

    [Fact]
    public void NumberComparedWithACheckbox_CountsTrueAsOne()
    {
        var result = Translate("Bool", "[Qty] = true");

        result.SettingsJson.Should().Contain("ToNumber(true)");
    }

    [Fact]
    public void NumberInAnd_IsReadAsTrueWhenNotZero()
    {
        var result = Translate("Bool", "[Done] and [Qty]");

        result.SettingsJson.Should().Contain("ToBool([Qty])");
    }

    [Fact]
    public void TextResultWhereANumberIsRequired_IsReadAsANumber()
    {
        Translate("Number", "[Status]").SettingsJson.Should().Contain("ToNumber([Status])");
    }

    [Fact]
    public void NumberResultWhereADurationIsRequired_IsReadAsHours()
    {
        var result = Translate("Duration", "[Qty]");

        result.SettingsJson.Should().Contain("Hours([Qty])");
        result.TypeCode.Should().Be("Formula_Duration");
    }

    [Fact]
    public void NumberResultWhereADurationIsRequired_InsideAVar_OnlyTheResultIsConverted()
    {
        var result = Translate("Duration", """
            var number q = [Qty] * 2;
            $q
            """);

        result.SettingsJson.Should().Contain("Hours($q)").And.Contain("var number q");
    }

    [Fact]
    public void TextAndUserBranches_TheTextBecomesAUser()
    {
        var result = Translate("User", "If([Status] = \"x\", ToUser(\"a@b.com\"), [Status])");

        result.SettingsJson.Should().Contain("ToUser([Status])");
        result.TypeCode.Should().Be("Formula_User");
    }

    [Fact]
    public void TextResultWhereAUserIsRequired_BecomesAUser()
    {
        Translate("User", "[Status]").SettingsJson.Should().Contain("ToUser([Status])");
    }

    [Fact]
    public void NumberAndTextBranches_AreJoinedAsText()
    {
        var result = Translate("Text", "If([Done], [Qty], [Status])");

        result.SettingsJson.Should().Contain("ToText([Qty])");
    }

    [Fact]
    public void LongConcatenationChain_IsFixedInOnePass()
    {
        var operands = string.Join(" & \"-\" & ", Enumerable.Repeat("[Qty]", 30));

        var result = Translate("Text", operands);

        result.Status.Should().Be(FormulaTranslationStatus.Adjusted);
    }

    [Fact]
    public void NumberVariableGivenText_IsConvertedOnTheWayIn()
    {
        var result = Translate("Text", """
            var number n = If([Status] != "", [Name], [Name]);
            If($n > 0, "yes", "no")
            """);

        result.SettingsJson.Should().Contain("ToNumber(If(");
    }

    [Fact]
    public void OneArgumentSplitOfAList_SplitsOnTheListDelimiter()
    {
        var result = Translate("Text", """
            ToText(Split(List(" ; ", If([Done] = true, "A"), If([Qty] > 1, "B"))))
            """);

        result.SettingsJson.Should().Contain("\\u0022B\\u0022)), \\u0022 ; \\u0022))");
    }

    [Fact]
    public void TextComparedWithTheCurrentUser_IsComparedWithTheUsersEmail()
    {
        var result = Translate("Bool", "[Name] = User()");

        result.SettingsJson.Should().Contain("UserToEmail(User())");
    }
}
