using FluentAssertions;
using PowerBase.Application.Import.FormulaTranslation;
using PowerBase.Application.Import.Qbl;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>WP3 (criteria / summaries) and WP4 (formula dialect) of the import completion work.</summary>
public class ImportCompletionFormulaAndCriteriaTests
{
    private readonly FormulaTranslator _sut = new(new FormulaEngine());

    private static List<AppField> Fields() =>
    [
        new() { Name = "Qty", TypeCode = "Number", Fid = 6 },
        new() { Name = "Name", TypeCode = "Text", Fid = 8 },
        new() { Name = "Status", TypeCode = "Text", Fid = 9 },
        new() { Name = "Owner", TypeCode = "User", Fid = 10 },
        new() { Name = "Email", TypeCode = "Text", Fid = 11 },
    ];

    // ---- WP4: formula dialect ----

    [Fact]
    public void Translate_TextEqualsNumber_WrapsTheNumberSide()
    {
        var result = _sut.Translate("Bool", "[Status] = [Qty]", Fields());

        result.Status.Should().Be(FormulaTranslationStatus.Adjusted);
        result.SettingsJson.Should().Contain("ToText([Qty])");
    }

    [Fact]
    public void Translate_ConcatAndComparisonInOneFormula_NeedsSeveralPasses()
    {
        var result = _sut.Translate("Text", "If([Status] = [Qty], [Name] & [Qty], \"x\")", Fields());

        result.Status.Should().Be(FormulaTranslationStatus.Adjusted);
        result.SettingsJson.Should().Contain("ToText([Qty])");
    }

    [Fact]
    public void Translate_TextWhereUserExpected_IsWrappedInToUser()
    {
        var result = _sut.Translate("Text", "UserToEmail([Email])", Fields());

        result.Status.Should().Be(FormulaTranslationStatus.Adjusted);
        result.SettingsJson.Should().Contain("ToUser([Email])");
    }

    [Fact]
    public void Translate_MultiBranchIf_IsSupportedAsWritten()
    {
        // The engine reads Quickbase's If(c1, v1, c2, v2, ..., else) itself, so the formula is stored exactly as the author wrote it.
        var result = _sut.Translate("Text", "If([Qty]=1, \"a\", [Qty]=2, \"b\", \"c\")", Fields());

        result.Status.Should().Be(FormulaTranslationStatus.Clean);
        result.SettingsJson.Should().Contain("If([Qty]=1, \\u0022a\\u0022, [Qty]=2");
    }

    [Fact]
    public void Translate_MultiBranchIfWithoutElse_IsSupportedAndBlankWhenNoConditionHolds()
    {
        var result = _sut.Translate("Text", "If([Qty]=1, \"a\", [Qty]=2, \"b\")", Fields());

        result.Status.Should().Be(FormulaTranslationStatus.Clean);
    }

    [Fact]
    public void Translate_GenuineUnknownField_StillNeedsManualReview()
    {
        var result = _sut.Translate("Text", "[Name] & [Qty] & [Missing]", Fields());

        result.Status.Should().Be(FormulaTranslationStatus.NeedsManualReview);
    }

    // ---- WP3: criteria ----

    private static string? Resolve(int id) => id switch { 8 => "Name", 11 => "Date", 12 => "Owner", _ => null };

    [Fact]
    public void Criteria_TrueValueAgainstCurrentUser_BecomesIsCurrentUser()
    {
        var result = QblCriteriaTranslator.Translate("{'12'.TV.'_curuser_'}", Resolve);

        result.Succeeded.Should().BeTrue(result.Problem);
        var c = result.Filter!.Conditions.Single();
        c.Operator.Should().Be("isCurrentUser");
        c.UseCurrentUser.Should().BeTrue();
    }

    [Fact]
    public void Criteria_NotTrueValueAgainstCurrentUser_BecomesIsNotCurrentUser()
    {
        var result = QblCriteriaTranslator.Translate("{'12'.XTV.'_curuser_'}", Resolve);

        result.Filter!.Conditions.Single().Operator.Should().Be("isNotCurrentUser");
    }

    [Theory]
    [InlineData("this wk", "duringCurrent", "1:week")]
    [InlineData("last 5 wk", "duringPrevious", "5:week")]
    [InlineData("last 60 d", "duringPrevious", "60:day")]
    [InlineData("this mon", "duringCurrent", "1:month")]
    [InlineData("next 2 mon", "duringNext", "2:month")]
    public void Criteria_InRange_BecomesDuringWithModeAndCount(string period, string mode, string value)
    {
        var result = QblCriteriaTranslator.Translate($"{{'11'.IR.'{period}'}}", Resolve, allowRelativeDates: true);

        result.Succeeded.Should().BeTrue(result.Problem);
        var c = result.Filter!.Conditions.Single();
        c.Operator.Should().Be("during");
        c.ValueMode.Should().Be(mode);
        c.Value.Should().Be(value);
    }

    [Fact]
    public void Criteria_NotInRange_BecomesNotDuring()
    {
        var result = QblCriteriaTranslator.Translate("{'11'.XIR.'this wk'}", Resolve, allowRelativeDates: true);

        result.Filter!.Conditions.Single().Operator.Should().Be("notDuring");
    }

    [Fact]
    public void Criteria_InRange_WhereRelativeDatesAreNotAllowed_IsRefused()
    {
        var result = QblCriteriaTranslator.Translate("{'11'.IR.'this wk'}", Resolve, allowRelativeDates: false);

        result.Succeeded.Should().BeFalse();
        result.Problem.Should().Contain("no PowerBase equivalent");
    }

    [Fact]
    public void Criteria_UnknownPeriod_IsRefused()
    {
        QblCriteriaTranslator.Translate("{'11'.IR.'fiscal quarter'}", Resolve, allowRelativeDates: true).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Criteria_TodayInSummaryCriteria_IsAllowedWhenRelativeDatesAre()
    {
        var result = QblCriteriaTranslator.Translate("{'11'.OBF.'today'}", Resolve, allowRelativeDates: true);

        result.Filter!.Conditions.Single().ValueMode.Should().Be("today");
    }

    // "N days ago" used to fall through every branch and was saved as the literal text "9 days ago" with no mode; the runtime
    // only resolves pastDays / futureDays, so the text reached SQL and a Summary or report crashed with a date-conversion error.
    [Theory]
    [InlineData("OAF", "9 days ago", "gte", "pastDays", "9")]
    [InlineData("AF", "365 days ago", "gt", "pastDays", "365")]
    [InlineData("OBF", "7 days ago", "lte", "pastDays", "7")]
    [InlineData("BF", "1 day ago", "lt", "pastDays", "1")]
    [InlineData("OAF", "0 days ago", "gte", "pastDays", "0")]
    [InlineData("OAF", "9 Days AGO", "gte", "pastDays", "9")]
    [InlineData("BF", "-35650 days ago", "lt", "futureDays", "35650")]
    [InlineData("OAF", "5 days from now", "gte", "futureDays", "5")]
    [InlineData("BF", "1 day from now", "lt", "futureDays", "1")]
    public void Criteria_DaysAgoOrFromNow_BecomesPastOrFutureDaysWithTheNumber(string op, string value, string pbOperator, string mode, string number)
    {
        var result = QblCriteriaTranslator.Translate($"{{'11'.{op}.'{value}'}}", Resolve, allowRelativeDates: true);

        result.Succeeded.Should().BeTrue(result.Problem);
        var c = result.Filter!.Conditions.Single();
        c.Operator.Should().Be(pbOperator);
        c.ValueMode.Should().Be(mode);
        c.Value.Should().Be(number);
    }

    [Fact]
    public void Criteria_DaysAgo_WhereRelativeDatesAreNotAllowed_IsRefusedNotSavedAsText()
    {
        var result = QblCriteriaTranslator.Translate("{'11'.OAF.'9 days ago'}", Resolve, allowRelativeDates: false);

        result.Succeeded.Should().BeFalse();
        result.Problem.Should().Contain("no PowerBase equivalent");
    }

    [Fact]
    public void Criteria_DaysAgoInsideALargerQuery_IsConvertedWhereItStands()
    {
        var result = QblCriteriaTranslator.Translate("{'8'.EX.'a'} AND {'11'.OAF.'30 days ago'}", Resolve, allowRelativeDates: true);

        result.Succeeded.Should().BeTrue(result.Problem);
        result.Filter!.Conditions.Should().HaveCount(2);
        result.Filter.Conditions[0].ValueMode.Should().BeNull();
        result.Filter.Conditions[1].ValueMode.Should().Be("pastDays");
        result.Filter.Conditions[1].Value.Should().Be("30");
    }
}
