using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// The rules that decide how much of a pipeline search Azure AI Search narrows down. The filter it builds must be a SUPERSET of
/// the SQL filter (the candidates are re-read from SQL with the complete filter), so a condition it cannot answer exactly is
/// left to SQL — in an AND the rest still narrows, in an OR everything stays open. The property tests at the end check the
/// superset rule itself: random values, every operator, the documented SQL meaning against a small model of the index.
/// </summary>
public class PipelineAiSearchPlannerTests
{
    private static AppField Field(int fid, string type = "Text", bool searchable = true, bool filterable = true, string name = "Status", string? slot = "str_01") =>
        new() { Id = fid + 100, Fid = fid, Name = name, TypeCode = type, IsSearchable = searchable, IsFilterable = filterable, SearchSlot = slot };

    private static FilterGroup Tree(params FilterCondition[] conditions) =>
        new() { Logic = "and", Nodes = conditions.Select(c => new FilterNode { Condition = c }).ToList() };

    private static FilterCondition Cond(int fid, string op = "eq", string? value = "Active", string? valueMode = null, long? valueFieldId = null, string? subField = null) =>
        new() { FieldId = fid, Operator = op, Value = value, ValueMode = valueMode, ValueFieldId = valueFieldId, SubField = subField };

    private static PipelineAiSearchPlan Plan(FilterGroup? tree, params AppField[] fields) =>
        PipelineAiSearchPlanner.Evaluate(tree, fields.Length == 0 ? [Field(7)] : fields);

    private static PipelineAiSearchPlan PlanWithPatterns(FilterGroup? tree, params AppField[] fields) =>
        PipelineAiSearchPlanner.Evaluate(tree, fields.Length == 0 ? [Field(7)] : fields, new PipelineAiSearchPlanOptions(PatternOperators: true));

    private static string Phrase(string slot, string text) => $"search.ismatch('\"{text}\"', '{slot}', 'full', 'any')";

    // ── text ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Equality_OnATextField_UsesAPhraseMatchOnItsSlot()
    {
        var plan = Plan(Tree(Cond(7)));

        Assert.True(plan.UseAiSearch);
        Assert.Null(plan.SqlReason);
        Assert.Equal(Phrase("str_01", "Active"), plan.ODataFilter);
        Assert.Equal(new long[] { 7 }, plan.FieldIds);
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("TextMultiLine")]
    [InlineData("Email")]
    [InlineData("Phone")]
    [InlineData("Url")]
    [InlineData("SingleSelect")]
    public void PlainTextTypes_AreAnsweredByTheIndex(string type) =>
        Assert.True(Plan(Tree(Cond(7)), Field(7, type)).UseAiSearch);

    [Fact]
    public void RichText_StaysInSql_BecauseTheIndexHoldsItsHtml() =>
        Assert.False(Plan(Tree(Cond(7)), Field(7, "RichText")).UseAiSearch);

    [Fact]
    public void InList_BecomesOneOrOfPhraseMatches() =>
        Assert.Equal($"({Phrase("str_01", "Active")} or {Phrase("str_01", "Pending")})", Plan(Tree(Cond(7, "in", "[\"Active\",\"Pending\"]"))).ODataFilter);

    [Fact]
    public void InList_AsCommaSeparatedText_IsReadTheSameWayTheSqlFilterReadsIt() =>
        Assert.Equal($"({Phrase("str_01", "Active")} or {Phrase("str_01", "Pending")})", Plan(Tree(Cond(7, "in", "Active, Pending"))).ODataFilter);

    [Fact]
    public void InList_WithOneValue_IsAPlainPhraseMatch() =>
        Assert.Equal(Phrase("str_01", "Active"), Plan(Tree(Cond(7, "in", "[\"Active\"]"))).ODataFilter);

    [Fact]
    public void InList_WithAValueTheIndexCannotMatch_LeavesTheConditionToSql() =>
        Assert.False(Plan(Tree(Cond(7, "in", "[\"Active\",\"the\"]"))).UseAiSearch);

    [Fact]
    public void QuotesBackslashesAndApostrophesInTheValue_AreEscapedForLuceneAndForTheODataString() =>
        Assert.Equal("search.ismatch('\"O''Brien \\\"Jr\\\" \\\\ team\"', 'str_01', 'full', 'any')", Plan(Tree(Cond(7, "eq", "O'Brien \"Jr\" \\ team"))).ODataFilter);

    [Fact]
    public void NonLatinText_IsEligible()
    {
        var plan = Plan(Tree(Cond(7, "eq", "ગુજરાતી")));

        Assert.True(plan.UseAiSearch);
        Assert.Contains("ગુજરાતી", plan.ODataFilter);
    }

    [Theory]
    [InlineData("the")]
    [InlineData("a and the")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData(null)]
    public void EqualityWithAValueTheIndexCannotMatch_IsLeftToSql(string? value) =>
        Assert.False(Plan(Tree(Cond(7, "eq", value))).UseAiSearch);

    [Fact]
    public void EqualityWithAValueLongerThanTheLimit_IsLeftToSql() =>
        Assert.False(Plan(Tree(Cond(7, "eq", new string('x', PipelineAiSearchPlanner.MaxValueLength + 1)))).UseAiSearch);

    [Fact]
    public void IsEmpty_OnText_IsNullOrEmptyString() =>
        Assert.Equal("(str_01 eq null or str_01 eq '')", Plan(Tree(Cond(7, "isEmpty", null))).ODataFilter);

    // ── columns ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFieldWithoutASlotOfItsOwn_IsLeftToSql_BecauseItsLegacyNameIsNotAColumnOfTheSharedIndex() =>
        Assert.False(Plan(Tree(Cond(7)), Field(7, slot: null)).UseAiSearch);

    [Fact]
    public void ASlotOfAnotherFamily_IsLeftToSql() =>
        Assert.False(Plan(Tree(Cond(7, "gt", "5")), Field(7, "Number", slot: "str_01")).UseAiSearch);

    [Fact]
    public void ATableWithItsOwnIndex_UsesTheFieldsOwnColumn()
    {
        // One field stored as "f_<fid>" marks the table as wide: every column is named after its field there.
        var plan = Plan(Tree(Cond(7), Cond(8, "gt", "5")), Field(7, slot: "f_7"), Field(8, "Number", slot: "f_8", name: "Amount"));

        Assert.Equal($"{Phrase("f_7", "Active")} and f_8 ge 4.9999", plan.ODataFilter);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ASearchableOrFilterableFieldIsInTheIndex(bool searchable, bool filterable) =>
        Assert.True(Plan(Tree(Cond(7)), Field(7, searchable: searchable, filterable: filterable)).UseAiSearch);

    [Fact]
    public void AFieldThatIsNeitherSearchableNorFilterable_IsNotInTheIndex() =>
        Assert.False(Plan(Tree(Cond(7)), Field(7, searchable: false, filterable: false)).UseAiSearch);

    // ── structure: partial answers ───────────────────────────────────────────────────────────

    [Fact]
    public void AndOfSeveralEligibleConditions_AreJoinedWithAnd()
    {
        var plan = Plan(Tree(Cond(7, value: "Active"), Cond(8, value: "North")), Field(7), Field(8, name: "Region", slot: "str_02"));

        Assert.Equal($"{Phrase("str_01", "Active")} and {Phrase("str_02", "North")}", plan.ODataFilter);
        Assert.Equal(new long[] { 7, 8 }, plan.FieldIds);
    }

    [Fact]
    public void OrAndNestedGroups_KeepTheirStructure()
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new() { Condition = Cond(7, value: "Active") },
                new() { Group = new FilterGroup { Logic = "or", Nodes = [new() { Condition = Cond(8, value: "North") }, new() { Condition = Cond(8, value: "South") }] } }
            ]
        };

        var plan = Plan(tree, Field(7), Field(8, name: "Region", slot: "str_02"));

        Assert.Equal($"{Phrase("str_01", "Active")} and ({Phrase("str_02", "North")} or {Phrase("str_02", "South")})", plan.ODataFilter);
    }

    [Fact]
    public void EmptyNestedGroups_AreIgnored()
    {
        var tree = new FilterGroup { Nodes = [new() { Group = new FilterGroup() }, new() { Condition = Cond(7) }] };

        Assert.Equal(Phrase("str_01", "Active"), Plan(tree).ODataFilter);
    }

    [Fact]
    public void AnAndKeepsWhatTheIndexCanAnswer_AndLeavesTheRestToSql()
    {
        // The formula condition cannot be answered by the index; SQL still checks it on every candidate. The Status condition narrows.
        var plan = Plan(Tree(Cond(7), Cond(8, value: "ORD1")), Field(7), Field(8, "Formula_Text", name: "Order no", slot: null));

        Assert.Equal(Phrase("str_01", "Active"), plan.ODataFilter);
        Assert.Equal(new long[] { 7 }, plan.FieldIds);
    }

    [Fact]
    public void AnOrWithOneBranchTheIndexCannotAnswer_IsLeftOpenCompletely()
    {
        // "Status is Active OR formula is X": a record matching only the formula branch must still be a candidate.
        var tree = new FilterGroup { Logic = "or", Nodes = [new() { Condition = Cond(7) }, new() { Condition = Cond(8, value: "ORD1") }] };

        var plan = Plan(tree, Field(7), Field(8, "Formula_Text", name: "Order no", slot: null));

        Assert.False(plan.UseAiSearch);
        Assert.Contains("no condition", plan.SqlReason);
    }

    [Fact]
    public void AnOpenOrInsideAnAnd_DoesNotWidenTheOtherConditions()
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new() { Condition = Cond(7) },
                new() { Group = new FilterGroup { Logic = "or", Nodes = [new() { Condition = Cond(8, "ne", "x") }, new() { Condition = Cond(7, value: "Pending") }] } }
            ]
        };

        var plan = Plan(tree, Field(7), Field(8, name: "Region", slot: "str_02"));

        Assert.Equal(Phrase("str_01", "Active"), plan.ODataFilter);
    }

    [Fact]
    public void ASearchWithNothingTheIndexCanAnswer_IsReadFromSql()
    {
        var plan = Plan(Tree(Cond(7, "ne", "Closed")));

        Assert.False(plan.UseAiSearch);
        Assert.NotNull(plan.SqlReason);
    }

    [Fact]
    public void NoFilter_IsReadFromSql() => Assert.False(Plan(null).UseAiSearch);

    [Fact]
    public void MoreConditionsThanTheLimit_SendTheWholeSearchToSql()
    {
        var conditions = Enumerable.Range(0, PipelineAiSearchPlanner.MaxConditions + 1).Select(_ => Cond(7)).ToArray();

        var plan = Plan(Tree(conditions));

        Assert.False(plan.UseAiSearch);
        Assert.Contains("more than", plan.SqlReason);
    }

    [Fact]
    public void ExactlyTheLimitOfConditions_IsStillAnsweredByTheIndex() =>
        Assert.True(Plan(Tree(Enumerable.Range(0, PipelineAiSearchPlanner.MaxConditions).Select(_ => Cond(7)).ToArray())).UseAiSearch);

    [Fact]
    public void MoreListValuesThanTheLimit_SendTheWholeSearchToSql()
    {
        var values = string.Join(",", Enumerable.Range(0, PipelineAiSearchPlanner.MaxInValues + 1).Select(i => $"value{i}"));

        Assert.False(Plan(Tree(Cond(7, "in", values))).UseAiSearch);
    }

    [Fact]
    public void ListValuesAcrossConditions_AreCountedTogether()
    {
        var half = string.Join(",", Enumerable.Range(0, PipelineAiSearchPlanner.MaxInValues / 2 + 1).Select(i => $"value{i}"));

        Assert.False(Plan(Tree(Cond(7, "in", half), Cond(7, "in", half))).UseAiSearch);
    }

    // ── conditions the index never answers ───────────────────────────────────────────────────

    [Theory]
    [InlineData("ne")]
    [InlineData("notIn")]
    [InlineData("notContains")]
    [InlineData("notStartsWith")]
    [InlineData("notEndsWith")]
    [InlineData("notWildcard")]
    [InlineData("isNotEmpty")]
    [InlineData("gt")]
    public void NegationsAndTextOrderings_AreLeftToSql(string op) =>
        Assert.False(PlanWithPatterns(Tree(Cond(7, op, "Active"))).UseAiSearch);

    [Theory]
    [InlineData("Formula_Text")]
    [InlineData("Formula_Number")]
    [InlineData("Lookup")]
    [InlineData("Summary")]
    [InlineData("Reference")]
    [InlineData("File")]
    [InlineData("Address")]
    [InlineData("User")]
    [InlineData("MultiUser")]
    [InlineData("Time")]
    [InlineData("Duration")]
    [InlineData("NumericRange")]
    [InlineData("DateRange")]
    public void TypesTheIndexDoesNotHoldFaithfully_AreLeftToSql(string type) =>
        Assert.False(Plan(Tree(Cond(7, "eq", "5")), Field(7, type, slot: "str_01")).UseAiSearch);

    [Fact]
    public void ASystemField_IsLeftToSql()
    {
        var field = Field(3, "Number", slot: "num_01");
        field.IsSystem = true;

        Assert.False(Plan(Tree(Cond(3, "eq", "5")), field).UseAiSearch);
    }

    [Fact]
    public void AConditionOnAFieldThatIsNotInTheTable_IsLeftToSql() =>
        Assert.False(Plan(Tree(Cond(99)), Field(7)).UseAiSearch);

    [Fact]
    public void AConditionOnASubField_IsLeftToSql() =>
        Assert.False(Plan(Tree(Cond(7, subField: "City"))).UseAiSearch);

    [Theory]
    [InlineData("field", 8)]
    [InlineData("field", null)]
    [InlineData("token", null)]
    public void AConditionThatComparesToAnotherFieldOrIsNotALiteral_IsLeftToSql(string mode, int? valueFieldId) =>
        Assert.False(Plan(Tree(Cond(7, valueMode: mode, valueFieldId: valueFieldId))).UseAiSearch);

    [Fact]
    public void ALiteralValueMode_IsAnsweredByTheIndex() =>
        Assert.True(Plan(Tree(Cond(7, valueMode: "literal"))).UseAiSearch);

    // ── numbers ──────────────────────────────────────────────────────────────────────────────

    private static AppField Number(string type = "Number") => Field(7, type, slot: "num_01", name: "Amount");

    [Fact]
    public void NumberEquality_IsARangeAroundTheValue() =>
        Assert.Equal("(num_01 ge 4.9999 and num_01 le 5.0001)", Plan(Tree(Cond(7, "eq", "5")), Number()).ODataFilter);

    [Fact]
    public void NumberOrderings_AreOpenEndedBounds_WidenedByTheMargin()
    {
        Assert.Equal("num_01 ge 4.9999", Plan(Tree(Cond(7, "gt", "5")), Number()).ODataFilter);
        Assert.Equal("num_01 ge 4.9999", Plan(Tree(Cond(7, "gte", "5")), Number()).ODataFilter);
        Assert.Equal("num_01 le 5.0001", Plan(Tree(Cond(7, "lt", "5")), Number()).ODataFilter);
        Assert.Equal("num_01 le 5.0001", Plan(Tree(Cond(7, "lte", "5")), Number()).ODataFilter);
    }

    [Fact]
    public void NumberInList_IsAnOrOfRanges() =>
        Assert.Equal("((num_01 ge 0.9999 and num_01 le 1.0001) or (num_01 ge 1.9999 and num_01 le 2.0001))", Plan(Tree(Cond(7, "in", "1, 2")), Number()).ODataFilter);

    [Fact]
    public void NumberIsEmpty_IsNull() => Assert.Equal("num_01 eq null", Plan(Tree(Cond(7, "isEmpty", null)), Number()).ODataFilter);

    [Theory]
    [InlineData("Number")]
    [InlineData("Currency")]
    [InlineData("Percent")]
    [InlineData("Rating")]
    public void NumericTypes_AreAnsweredByTheIndex(string type) => Assert.True(Plan(Tree(Cond(7, "gt", "5")), Number(type)).UseAiSearch);

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void ANumberThatIsNoNumber_IsLeftToSql(string? value) => Assert.False(Plan(Tree(Cond(7, "eq", value)), Number()).UseAiSearch);

    [Fact]
    public void ALargeNumber_GetsAMarginRelativeToItsSize()
    {
        // 14 integer digits is what DECIMAL(18,4) holds; a double rounds them in the last places.
        var plan = Plan(Tree(Cond(7, "eq", "99999999999999.9999")), Number());

        // The double nearest the value is 1E14; the margin is 1E-12 of it (100), far above the double's own rounding (about 0.016).
        Assert.Equal("(num_01 ge 99999999999900 and num_01 le 100000000000100)", plan.ODataFilter);
    }

    [Fact]
    public void NumberContains_IsLeftToSql() => Assert.False(PlanWithPatterns(Tree(Cond(7, "contains", "5")), Number()).UseAiSearch);

    // ── dates ────────────────────────────────────────────────────────────────────────────────

    private static AppField DateField(string type = "Date") => Field(7, type, slot: "date_01", name: "Due");

    [Fact]
    public void DateEquality_IsTheDayWidenedByADayEitherSide() =>
        Assert.Equal("(date_01 ge 2026-10-25T00:00:00.000Z and date_01 le 2026-10-28T00:00:00.000Z)", Plan(Tree(Cond(7, "eq", "2026-10-26")), DateField()).ODataFilter);

    [Theory]
    [InlineData("gt")]
    [InlineData("gte")]
    [InlineData("date_gt")]
    [InlineData("date_gte")]
    public void DateAfter_IsAnOpenLowerBound(string op) =>
        Assert.Equal("date_01 ge 2026-10-25T00:00:00.000Z", Plan(Tree(Cond(7, op, "2026-10-26")), DateField("DateTime")).ODataFilter);

    [Theory]
    [InlineData("lt")]
    [InlineData("lte")]
    [InlineData("date_lt")]
    [InlineData("date_lte")]
    public void DateBefore_IsAnOpenUpperBound(string op) =>
        Assert.Equal("date_01 le 2026-10-28T00:00:00.000Z", Plan(Tree(Cond(7, op, "2026-10-26")), DateField("DateTime")).ODataFilter);

    [Fact]
    public void DateWithATimeAndAnOffset_IsReadInUniversalTime() =>
        Assert.Equal("(date_01 ge 2026-10-25T04:30:00.000Z and date_01 le 2026-10-28T04:30:00.000Z)",
            Plan(Tree(Cond(7, "eq", "2026-10-26T10:00:00+05:30")), DateField("DateTime")).ODataFilter);

    [Fact]
    public void DateInList_IsAnOrOfWindows() =>
        Assert.Equal("((date_01 ge 2026-10-25T00:00:00.000Z and date_01 le 2026-10-28T00:00:00.000Z) or (date_01 ge 2026-10-26T00:00:00.000Z and date_01 le 2026-10-29T00:00:00.000Z))",
            Plan(Tree(Cond(7, "in", "2026-10-26, 2026-10-27")), DateField()).ODataFilter);

    [Fact]
    public void DateIsEmpty_IsNull() => Assert.Equal("date_01 eq null", Plan(Tree(Cond(7, "isEmpty", null)), DateField()).ODataFilter);

    [Theory]
    [InlineData("@relative-date|today|0|India Standard Time")]
    [InlineData("@relative-period|current|1|week|India Standard Time")]
    [InlineData("not a date")]
    [InlineData("")]
    public void ARelativeOrUnreadableDate_IsLeftToSql(string value) =>
        Assert.False(Plan(Tree(Cond(7, "eq", value)), DateField()).UseAiSearch);

    [Theory]
    [InlineData("ne")]
    [InlineData("date_ne")]
    [InlineData("isNotEmpty")]
    public void DateNegations_AreLeftToSql(string op) => Assert.False(Plan(Tree(Cond(7, op, "2026-10-26")), DateField()).UseAiSearch);

    // ── yes / no ─────────────────────────────────────────────────────────────────────────────

    private static AppField Bool() => Field(7, "Boolean", slot: "bool_01", name: "Done");

    [Theory]
    [InlineData("true", "bool_01 eq true")]
    [InlineData("True", "bool_01 eq true")]
    [InlineData("1", "bool_01 eq true")]
    [InlineData("false", "bool_01 eq false")]
    [InlineData("0", "bool_01 eq false")]
    public void BooleanEquality(string value, string expected) => Assert.Equal(expected, Plan(Tree(Cond(7, "eq", value)), Bool()).ODataFilter);

    [Fact]
    public void BooleanIn_IsAnOr() =>
        Assert.Equal("(bool_01 eq true or bool_01 eq false)", Plan(Tree(Cond(7, "in", "true,false")), Bool()).ODataFilter);

    [Fact]
    public void BooleanIsEmpty_IsNull() => Assert.Equal("bool_01 eq null", Plan(Tree(Cond(7, "isEmpty", null)), Bool()).ODataFilter);

    [Fact]
    public void ABooleanThatIsNoBoolean_IsLeftToSql() => Assert.False(Plan(Tree(Cond(7, "eq", "maybe")), Bool()).UseAiSearch);

    [Fact]
    public void ABlankMeansFalse_BothBranchesAreAnsweredByTheIndex()
    {
        // The pipeline words a checkbox's "is false" as "is false OR is blank" so a never-set checkbox counts as unchecked.
        var tree = new FilterGroup { Logic = "or", Nodes = [new() { Condition = Cond(7, "eq", "false") }, new() { Condition = Cond(7, "isEmpty", null) }] };

        Assert.Equal("bool_01 eq false or bool_01 eq null", Plan(tree, Bool()).ODataFilter);
    }

    // ── several values ───────────────────────────────────────────────────────────────────────

    private static AppField Multi(string type = "MultiSelect") => Field(7, type, slot: "str_02", name: "Colours");

    [Fact]
    public void MultiIncludes_AnyPickedValueMatches() =>
        Assert.Equal($"({Phrase("str_02", "Red")} or {Phrase("str_02", "Blue")})", Plan(Tree(Cond(7, "includes", "Red, Blue")), Multi()).ODataFilter);

    [Fact]
    public void MultiEquality_EveryPickedValueIsRequired() =>
        Assert.Equal($"{Phrase("str_02", "Red")} and {Phrase("str_02", "Blue")}", Plan(Tree(Cond(7, "eq", "Red,Blue")), Multi("CheckboxGroup")).ODataFilter);

    [Fact]
    public void MultiEquality_DropsAPickTheIndexCannotMatch_ButKeepsTheRest() =>
        Assert.Equal(Phrase("str_02", "Red"), Plan(Tree(Cond(7, "eq", "Red,the")), Multi()).ODataFilter);

    [Fact]
    public void MultiIsEmpty_IncludesAnEmptyList() =>
        Assert.Equal("(str_02 eq null or str_02 eq '' or str_02 eq '[]')", Plan(Tree(Cond(7, "isEmpty", null)), Multi()).ODataFilter);

    [Fact]
    public void MultiNotIncludes_IsLeftToSql() => Assert.False(Plan(Tree(Cond(7, "notIncludes", "Red")), Multi()).UseAiSearch);

    // ── pattern operators (contains / starts with / ends with / wildcard) ────────────────────

    [Fact]
    public void ThePipelinesOwnSetting_HasPatternOperatorsOnByDefault() =>
        Assert.True(new PowerBase.Application.Common.Configurations.PipelineExecutionOptions().AiSearchPatternOperators);

    [Theory]
    [InlineData("contains")]
    [InlineData("startsWith")]
    [InlineData("endsWith")]
    [InlineData("wildcard")]
    public void PatternOperators_AreLeftToSqlUnlessSwitchedOn(string op)
    {
        Assert.False(Plan(Tree(Cond(7, op, "Active"))).UseAiSearch);
        Assert.True(PlanWithPatterns(Tree(Cond(7, op, "Active"))).UseAiSearch);
    }

    [Fact]
    public void Contains_AsksForTheWordInsideAnyWordOfTheText() =>
        Assert.Equal("search.ismatch('/.*ronak.*/', 'str_01', 'full', 'any')", PlanWithPatterns(Tree(Cond(7, "contains", "Ronak"))).ODataFilter);

    [Fact]
    public void Contains_WithSeveralWords_RequiresEachOfThem() =>
        Assert.Equal("search.ismatch('/.*john.*/', 'str_01', 'full', 'any') and search.ismatch('/.*smith.*/', 'str_01', 'full', 'any')",
            PlanWithPatterns(Tree(Cond(7, "contains", "John Smith"))).ODataFilter);

    [Fact]
    public void Contains_WithSymbols_AsksForEachRunOfLettersAndDigits() =>
        // The analyzer splits "ab-cd" and "x@y.com" into words; a run lies inside one of them whatever it is joined to.
        Assert.Equal("search.ismatch('/.*ab.*/', 'str_01', 'full', 'any') and search.ismatch('/.*cd.*/', 'str_01', 'full', 'any')",
            PlanWithPatterns(Tree(Cond(7, "contains", "ab-cd"))).ODataFilter);

    [Theory]
    [InlineData("the")]       // a stop word
    [InlineData("he")]        // a fragment of one ("the", "they"): the index dropped those words
    [InlineData("a")]
    [InlineData("of")]
    [InlineData("---")]
    [InlineData("")]
    public void Contains_OfOnlyStopWordFragmentsOrNoWord_IsLeftToSql(string value) =>
        Assert.False(PlanWithPatterns(Tree(Cond(7, "contains", value))).UseAiSearch);

    [Fact]
    public void Contains_SkipsAStopWordFragment_ButKeepsTheOtherWords() =>
        Assert.Equal("search.ismatch('/.*cat.*/', 'str_01', 'full', 'any')", PlanWithPatterns(Tree(Cond(7, "contains", "the cat"))).ODataFilter);

    [Theory]
    [InlineData("100%")]
    [InlineData("a_b")]
    [InlineData("a[bc]d")]
    public void ALikeCharacterInTheValue_LeavesContainsToSql_BecauseThereItIsAPattern(string value)
    {
        Assert.False(PlanWithPatterns(Tree(Cond(7, "contains", value))).UseAiSearch);
        Assert.False(PlanWithPatterns(Tree(Cond(7, "startsWith", value))).UseAiSearch);
        Assert.False(PlanWithPatterns(Tree(Cond(7, "endsWith", value))).UseAiSearch);
    }

    [Fact]
    public void StartsWith_AnchorsTheFirstWordToTheStartOfAWord() =>
        Assert.Equal("search.ismatch('/john.*/', 'str_01', 'full', 'any') and search.ismatch('/.*sm.*/', 'str_01', 'full', 'any')",
            PlanWithPatterns(Tree(Cond(7, "startsWith", "John sm"))).ODataFilter);

    [Fact]
    public void StartsWith_WhenTheValueDoesNotStartWithAWord_AnchorsNothing() =>
        Assert.Equal("search.ismatch('/.*john.*/', 'str_01', 'full', 'any')", PlanWithPatterns(Tree(Cond(7, "startsWith", "-john"))).ODataFilter);

    [Fact]
    public void EndsWith_AnchorsTheLastWordToTheEndOfAWord() =>
        Assert.Equal("search.ismatch('/.*john.*/', 'str_01', 'full', 'any') and search.ismatch('/.*son/', 'str_01', 'full', 'any')",
            PlanWithPatterns(Tree(Cond(7, "endsWith", "john son"))).ODataFilter);

    [Fact]
    public void Wildcard_AsksForEachLiteralPieceBetweenTheWildcards() =>
        Assert.Equal("search.ismatch('/.*jo.*/', 'str_01', 'full', 'any') and search.ismatch('/.*hn.*/', 'str_01', 'full', 'any')",
            PlanWithPatterns(Tree(Cond(7, "wildcard", "jo*hn"))).ODataFilter);

    [Fact]
    public void Wildcard_WithALiteralLikeCharacter_IsStillAnswered_BecauseSqlEscapesItThere() =>
        Assert.Equal("search.ismatch('/.*ab.*/', 'str_01', 'full', 'any') and search.ismatch('/.*cd.*/', 'str_01', 'full', 'any')",
            PlanWithPatterns(Tree(Cond(7, "wildcard", "ab_cd"))).ODataFilter);

    [Fact]
    public void ARunOfNonAsciiLetters_IsNotAWord_ButTheAsciiAroundItIs() =>
        Assert.Equal("search.ismatch('/.*caf.*/', 'str_01', 'full', 'any')", PlanWithPatterns(Tree(Cond(7, "contains", "café"))).ODataFilter);

    [Fact]
    public void ManyWords_KeepTheLongestFive_InTheOrderTheyAreTyped() =>
        Assert.Equal(
            "search.ismatch('/.*alpha.*/', 'str_01', 'full', 'any') and search.ismatch('/.*bravo.*/', 'str_01', 'full', 'any') and " +
            "search.ismatch('/.*charlie.*/', 'str_01', 'full', 'any') and search.ismatch('/.*delta.*/', 'str_01', 'full', 'any') and " +
            "search.ismatch('/.*foxtrot.*/', 'str_01', 'full', 'any')",
            PlanWithPatterns(Tree(Cond(7, "contains", "alpha bravo charlie delta echo foxtrot"))).ODataFilter);

    [Fact]
    public void PatternOperators_OnAMultiValueField_ReadItsJsonText() =>
        Assert.Equal("search.ismatch('/.*red.*/', 'str_02', 'full', 'any')", PlanWithPatterns(Tree(Cond(7, "contains", "Red")), Multi()).ODataFilter);

    // ── phrase helpers ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Phrase_IsACaseInsensitivePhraseMatchOverTheColumn() =>
        Assert.Equal("search.ismatch('\"a b\"', 'f_3', 'full', 'any')", PipelineAiSearchPlanner.Phrase("f_3", "a b"));

    [Theory]
    [InlineData("Active", true)]
    [InlineData("a b", true)]
    [InlineData("ISBN-10", true)]
    [InlineData("the", false)]
    [InlineData("the of", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("!!!", false)]
    [InlineData(null, false)]
    public void IsMatchable_NeedsAWordTheIndexKeeps(string? value, bool expected) =>
        Assert.Equal(expected, PipelineAiSearchPlanner.IsMatchable(value));

    [Theory]
    [InlineData("[\"a\",\"b\"]", new[] { "a", "b" })]
    [InlineData("a, b", new[] { "a", "b" })]
    [InlineData("[1,2]", new[] { "1", "2" })]
    [InlineData("", new string[0])]
    public void ParseValueList_ReadsJsonAndCommaLists(string raw, string[] expected) =>
        Assert.Equal(expected, PipelineAiSearchPlanner.ParseValueList(raw));

    // ── the superset rule ────────────────────────────────────────────────────────────────────
    //
    // For random values and every operator the planner answers: whenever SQL's documented meaning says a record matches, the filter
    // the planner built must match it too, judged by a small model of the index (lower-cased words, stop words dropped, phrase =
    // adjacent words, regular expression = whole word, ranges over the typed columns). The model splits text at EVERY symbol —
    // the finest splitting an analyzer could do — so "lies inside one word" is tested in its hardest case.

    private static readonly string[] StopWordList =
        ["a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "if", "in", "into", "is", "it", "no", "not", "of", "on", "or", "such",
         "that", "the", "their", "then", "there", "these", "they", "this", "to", "was", "will", "with"];

    private static IEnumerable<string> Words(string? text)
    {
        if (text == null) yield break;
        var word = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant() + " ")
        {
            var isIdeograph = ch >= '一' && ch <= '鿿';
            if (char.IsLetterOrDigit(ch) && !isIdeograph) { word.Append(ch); continue; }
            if (word.Length > 0) { var w = word.ToString(); word.Clear(); if (!StopWordList.Contains(w)) yield return w; }
            if (isIdeograph) yield return ch.ToString();
        }
    }

    private static bool IsMatch(string lucene, object? columnValue)
    {
        if (columnValue is not string text) return false;
        var words = Words(text).ToList();
        if (lucene.StartsWith('"'))
        {
            var phrase = Words(lucene[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\")).ToList();
            if (phrase.Count == 0) return false;
            for (var i = 0; i + phrase.Count <= words.Count; i++)
                if (phrase.SequenceEqual(words.Skip(i).Take(phrase.Count))) return true;
            return false;
        }
        var regex = new Regex("^(?:" + lucene[1..^1] + ")$", RegexOptions.CultureInvariant);
        return words.Any(regex.IsMatch);
    }

    /// <summary>Evaluates the planner's filter (only the shapes it produces) against one document of typed column values.</summary>
    private static bool Matches(string filter, IReadOnlyDictionary<string, object?> doc)
    {
        var parser = new FilterParser(filter, doc);
        var result = parser.ParseOr();
        parser.End();
        return result;
    }

    private sealed class FilterParser(string text, IReadOnlyDictionary<string, object?> doc)
    {
        private int _i;

        public bool ParseOr()
        {
            var value = ParseAnd();
            while (Word("or")) value |= ParseAnd();
            return value;
        }

        private bool ParseAnd()
        {
            var value = ParseAtom();
            while (Word("and")) value &= ParseAtom();
            return value;
        }

        private bool ParseAtom()
        {
            Skip();
            if (text[_i] == '(')
            {
                _i++;
                var inner = ParseOr();
                Skip();
                if (text[_i++] != ')') throw new FormatException($"')' expected at {_i} in {text}");
                return inner;
            }
            if (text.AsSpan(_i).StartsWith("search.ismatch("))
            {
                _i += "search.ismatch(".Length;
                var lucene = Quoted();
                Comma();
                var column = Quoted();
                Comma(); Quoted(); Comma(); Quoted();
                Skip();
                _i++;   // ')'
                return IsMatch(lucene, doc.GetValueOrDefault(column));
            }
            return Comparison();
        }

        private bool Comparison()
        {
            var column = Token();
            var op = Token();
            var literal = Token();
            var value = doc.GetValueOrDefault(column);
            if (literal == "null") return value == null;
            if (literal == "''") return value is string s && s.Length == 0;
            if (literal is "true" or "false") return value is bool b && b == (literal == "true");
            if (value is double d) return op == "ge" ? d >= double.Parse(literal, CultureInfo.InvariantCulture) : d <= double.Parse(literal, CultureInfo.InvariantCulture);
            if (value is DateTimeOffset at)
            {
                var bound = DateTimeOffset.Parse(literal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                return op == "ge" ? at >= bound : at <= bound;
            }
            return false;   // a missing value is in no range
        }

        private string Quoted()
        {
            Skip();
            _i++;   // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (text[_i] == '\'' && _i + 1 < text.Length && text[_i + 1] == '\'') { sb.Append('\''); _i += 2; continue; }
                if (text[_i] == '\'') { _i++; break; }
                sb.Append(text[_i++]);
            }
            return sb.ToString();
        }

        private void Comma() { Skip(); _i++; }

        private string Token()
        {
            Skip();
            var start = _i;
            while (_i < text.Length && !char.IsWhiteSpace(text[_i]) && text[_i] != ')') _i++;
            return text[start.._i];
        }

        private bool Word(string word)
        {
            Skip();
            if (!text.AsSpan(_i).StartsWith(word + " ")) return false;
            _i += word.Length;
            return true;
        }

        private void Skip() { while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++; }

        public void End() { Skip(); Assert.Equal(text.Length, _i); }
    }

    private const string SymbolAlphabet = "abcXYZ019 -'@._,é中ß";

    private static string RandomText(Random random, int maxLength = 14, string? alphabet = null)
    {
        alphabet ??= SymbolAlphabet;
        var length = random.Next(0, maxLength + 1);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(alphabet[random.Next(alphabet.Length)]);
        return sb.ToString();
    }

    /// <summary>Random text made of the pieces most likely to expose a gap: stop words, words with symbols, mixed case.</summary>
    private static string RandomDocument(Random random)
    {
        string[] pieces = ["the", "of", "Bank", "India", "john", "Smith", "o'brien", "ab-cd", "x@y.com", "3.5", "a_b", "café", "中文", "ISBN-10", "Straße", "100%", "and"];
        var count = random.Next(0, 5);
        return string.Join(random.Next(3) == 0 ? "  " : " ", Enumerable.Range(0, count).Select(_ => pieces[random.Next(pieces.Length)]));
    }

    private static string Slice(Random random, string text)
    {
        if (text.Length == 0) return text;
        var start = random.Next(text.Length);
        return text.Substring(start, random.Next(0, text.Length - start + 1));
    }

    private static string MixCase(Random random, string text) =>
        new(text.Select(c => random.Next(2) == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)).ToArray());

    [Fact]
    public void Property_TextOperators_TheFilterNeverLeavesOutARecordSqlMatches()
    {
        var random = new Random(20261027);
        var pushed = 0;
        var matched = 0;
        var operators = new[] { "eq", "in", "isEmpty", "contains", "startsWith", "endsWith", "wildcard" };

        for (var n = 0; n < 40000; n++)
        {
            var op = operators[random.Next(operators.Length)];
            var doc = random.Next(12) == 0 ? null : random.Next(3) == 0 ? RandomText(random) : RandomDocument(random);
            var chosen = Slice(random, doc ?? "");
            var value = op switch
            {
                "eq" or "in" => random.Next(2) == 0 ? MixCase(random, doc ?? "") : RandomText(random),
                "wildcard" => string.Concat(Slice(random, doc ?? "").Select(c => random.Next(6) == 0 ? "*" : random.Next(9) == 0 ? "?" : c.ToString())),
                _ => random.Next(3) == 0 ? RandomText(random, 6, "abcXYZ019 -'@.,é中") : chosen
            };
            if (op is "contains" or "startsWith" or "endsWith") value = value.Replace("%", "").Replace("[", "").Replace("_", "");
            if (op is "startsWith" && doc != null && random.Next(2) == 0) value = doc[..Math.Min(doc.Length, random.Next(1, 8))].Replace("_", "");
            if (op is "endsWith" && doc != null && random.Next(2) == 0) value = doc[Math.Max(0, doc.Length - random.Next(1, 8))..].Replace("_", "");

            var condition = op switch
            {
                "in" => Cond(7, "in", string.Join(",", value, "zzz9")),
                "isEmpty" => Cond(7, "isEmpty", null),
                _ => Cond(7, op, value)
            };

            // SQL's meaning (LIKE, case-insensitive; = case-insensitive; blank = NULL or '').
            bool sql;
            var lower = doc?.ToLowerInvariant();
            switch (op)
            {
                case "eq": sql = doc != null && string.Equals(doc.TrimEnd(), value.TrimEnd(), StringComparison.OrdinalIgnoreCase); break;
                case "in":
                {
                    // The list is read as the SQL filter reads it: comma separated, blanks dropped, each entry trimmed.
                    var listed = string.Join(",", value, "zzz9").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    sql = doc != null && listed.Any(v => string.Equals(doc.TrimEnd(), v.TrimEnd(), StringComparison.OrdinalIgnoreCase));
                    break;
                }
                case "isEmpty": sql = doc == null || doc.Length == 0; break;
                case "contains": sql = lower != null && lower.Contains(value.ToLowerInvariant(), StringComparison.Ordinal); break;
                case "startsWith": sql = lower != null && lower.StartsWith(value.ToLowerInvariant(), StringComparison.Ordinal); break;
                case "endsWith": sql = lower != null && lower.EndsWith(value.ToLowerInvariant(), StringComparison.Ordinal); break;
                default:
                {
                    var pattern = "^" + string.Concat(value.Select(c => c == '*' ? ".*" : c == '?' ? "." : Regex.Escape(c.ToString()))) + "$";
                    sql = doc != null && Regex.IsMatch(doc, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
                    break;
                }
            }

            var plan = PlanWithPatterns(Tree(condition));
            if (!plan.UseAiSearch) continue;   // SQL answers: nothing to be left out
            pushed++;
            var column = new Dictionary<string, object?> { ["str_01"] = doc };
            var ai = Matches(plan.ODataFilter!, column);
            if (sql) matched++;
            Assert.True(!sql || ai, $"op={op} value='{value}' doc='{doc}' filter={plan.ODataFilter}");
        }

        Assert.True(pushed > 5000, $"only {pushed} cases reached the index: the test would prove little");
        Assert.True(matched > 2000, $"only {matched} cases had a SQL match: the test would prove little");
    }

    [Fact]
    public void Property_NumberOperators_TheFilterNeverLeavesOutARecordSqlMatches()
    {
        var random = new Random(20261028);
        var operators = new[] { "eq", "gt", "gte", "lt", "lte", "in" };
        decimal Random4(bool large) => large
            ? Math.Round((decimal)(random.NextDouble() * 99999999999999.0), 4)
            : Math.Round((decimal)(random.NextDouble() * 2000 - 1000), 4);
        var matched = 0;

        for (var n = 0; n < 40000; n++)
        {
            var large = random.Next(4) == 0;
            var doc = random.Next(15) == 0 ? (decimal?)null : Random4(large);
            var value = doc.HasValue && random.Next(2) == 0 ? doc.Value + random.Next(-1, 2) * 0.0001m : Random4(large);
            var op = operators[random.Next(operators.Length)];
            var text = value.ToString(CultureInfo.InvariantCulture);
            var condition = op == "in" ? Cond(7, "in", $"{text}, 12345.5") : Cond(7, op, text);

            var sql = doc.HasValue && op switch
            {
                "eq" => doc.Value == value,
                "gt" => doc.Value > value,
                "gte" => doc.Value >= value,
                "lt" => doc.Value < value,
                "lte" => doc.Value <= value,
                _ => doc.Value == value || doc.Value == 12345.5m
            };

            var plan = Plan(Tree(condition), Number());
            Assert.True(plan.UseAiSearch);
            var ai = Matches(plan.ODataFilter!, new Dictionary<string, object?> { ["num_01"] = doc.HasValue ? (double)doc.Value : null });
            if (sql) matched++;
            Assert.True(!sql || ai, $"op={op} value={text} doc={doc} filter={plan.ODataFilter}");
        }
        Assert.True(matched > 3000);
    }

    [Fact]
    public void Property_DateOperators_TheFilterNeverLeavesOutARecordSqlMatches()
    {
        var random = new Random(20261029);
        var operators = new[] { "eq", "date_eq", "gt", "date_gt", "gte", "date_gte", "lt", "date_lt", "lte", "date_lte" };
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var matched = 0;

        for (var n = 0; n < 40000; n++)
        {
            var doc = random.Next(15) == 0 ? (DateTime?)null : origin.AddMinutes(random.Next(0, 60 * 24 * 40));
            var day = origin.AddDays(random.Next(0, 40));
            var instant = random.Next(2) == 0 ? day : day.AddMinutes(random.Next(0, 1440));
            var op = operators[random.Next(operators.Length)];
            var text = instant.TimeOfDay == TimeSpan.Zero && random.Next(2) == 0 ? instant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : instant.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var at = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

            // SQL's meanings, all of them (each operator is one of these): a day window anchored at the value, or the instant itself.
            var d = doc;
            var sql = d.HasValue && op switch
            {
                "eq" or "date_eq" => (d >= at && d < at.AddDays(1)) || d == at,
                "gt" or "date_gt" => d >= at.AddDays(1) || d > at,
                "gte" or "date_gte" => d >= at,
                "lt" or "date_lt" => d < at,
                _ => d < at.AddDays(1) || d <= at
            };

            var plan = Plan(Tree(Cond(7, op, text)), DateField("DateTime"));
            Assert.True(plan.UseAiSearch);
            var ai = Matches(plan.ODataFilter!, new Dictionary<string, object?> { ["date_01"] = doc.HasValue ? new DateTimeOffset(doc.Value, TimeSpan.Zero) : null });
            if (sql) matched++;
            Assert.True(!sql || ai, $"op={op} value={text} doc={doc:O} filter={plan.ODataFilter}");
        }
        Assert.True(matched > 3000);
    }

    [Fact]
    public void Property_MultiValueOperators_TheFilterNeverLeavesOutARecordSqlMatches()
    {
        var random = new Random(20261030);
        string[] choices = ["Red", "Blue", "Dark Green", "the", "Off-white", "Mr. X"];
        var matched = 0;

        for (var n = 0; n < 30000; n++)
        {
            var stored = choices.Where(_ => random.Next(2) == 0).ToList();
            var doc = random.Next(3) == 0 ? string.Join(",", stored) : "[" + string.Join(",", stored.Select(s => $"\"{s}\"")) + "]";
            var picked = choices.Where(_ => random.Next(3) == 0).Select(c => random.Next(2) == 0 ? c.ToUpperInvariant() : c).ToList();
            if (picked.Count == 0) picked.Add("Red");
            var op = random.Next(2) == 0 ? "includes" : "eq";

            var sql = op == "includes"
                ? picked.Any(p => stored.Contains(p, StringComparer.OrdinalIgnoreCase))
                : picked.Select(p => p.ToLowerInvariant()).Distinct().Count() == stored.Select(s => s.ToLowerInvariant()).Distinct().Count()
                  && picked.All(p => stored.Contains(p, StringComparer.OrdinalIgnoreCase));

            var plan = Plan(Tree(Cond(7, op, string.Join(",", picked))), Multi());
            if (!plan.UseAiSearch) continue;
            var ai = Matches(plan.ODataFilter!, new Dictionary<string, object?> { ["str_02"] = doc });
            if (sql) matched++;
            Assert.True(!sql || ai, $"op={op} picked={string.Join("|", picked)} doc={doc} filter={plan.ODataFilter}");
        }
        Assert.True(matched > 1000);
    }


    [Fact]
    public void Property_AnAndOrOrOfConditions_StaysASupersetOfTheSqlTree()
    {
        // Mixed trees of answerable and unanswerable conditions: a record the full SQL tree matches is always a candidate.
        var random = new Random(20261031);
        var fields = new[] { Field(7), Field(8, "Number", slot: "num_01", name: "Amount"), Field(9, "Formula_Text", slot: null, name: "Calc") };
        var matched = 0;
        var pushed = 0;

        for (var n = 0; n < 30000; n++)
        {
            var text = random.Next(2) == 0 ? "Active" : "Closed";
            var number = random.Next(0, 10);
            var doc = new Dictionary<string, object?> { ["str_01"] = random.Next(2) == 0 ? "active" : "Closed", ["num_01"] = (double)random.Next(0, 10) };
            // The formula's value is SQL's business: each such condition gets its own coin.
            var formulaValues = new Dictionary<FilterCondition, bool>(ReferenceEqualityComparer.Instance);

            FilterCondition Make()
            {
                switch (random.Next(3))
                {
                    case 0: return Cond(7, "eq", text);
                    case 1: return Cond(8, "gt", number.ToString(CultureInfo.InvariantCulture));
                    default:
                    {
                        var formula = Cond(9, "eq", "x");
                        formulaValues[formula] = random.Next(2) == 0;
                        return formula;
                    }
                }
            }

            FilterGroup Group(int conditions, bool nested) => new()
            {
                Logic = random.Next(2) == 0 ? "or" : "and",
                Nodes = Enumerable.Range(0, conditions).Select(_ => new FilterNode { Condition = Make() })
                    .Concat(nested ? [new FilterNode { Group = Group(random.Next(1, 3), nested: false) }] : []).ToList()
            };

            bool Leaf(FilterCondition c) => c.FieldId switch
            {
                7 => string.Equals((string?)doc["str_01"], c.Value, StringComparison.OrdinalIgnoreCase),
                8 => (double)doc["num_01"]! > double.Parse(c.Value!, CultureInfo.InvariantCulture),
                _ => formulaValues[c]
            };

            bool Sql(FilterGroup group)
            {
                var results = new List<bool>();
                foreach (var node in group.Nodes)
                {
                    if (node.Condition != null) results.Add(Leaf(node.Condition));
                    else if (node.Group!.Nodes.Count > 0) results.Add(Sql(node.Group));
                }
                if (results.Count == 0) return true;
                return string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase) ? results.Any(r => r) : results.All(r => r);
            }

            var tree = Group(random.Next(1, 4), nested: random.Next(2) == 0);
            var sqlMatch = Sql(tree);
            var plan = Plan(tree, fields);
            if (!plan.UseAiSearch) continue;
            pushed++;
            if (sqlMatch) matched++;
            Assert.True(!sqlMatch || Matches(plan.ODataFilter!, doc), $"filter={plan.ODataFilter}");
        }
        Assert.True(pushed > 3000, $"only {pushed} trees reached the index");
        Assert.True(matched > 1000);
    }
}
