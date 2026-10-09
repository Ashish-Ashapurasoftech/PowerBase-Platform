using System.Text.Json;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// The rules that decide whether a pipeline search may be answered by Azure AI Search at all. Every case that answers "no"
/// protects against the index returning less (or other) than SQL would: such a search is read from SQL. A "yes" produces the
/// exact AI filter.
/// </summary>
public class PipelineAiSearchPlannerTests
{
    private static AppField Field(int fid, string type = "Text", bool searchable = true, bool filterable = true, string name = "Status") =>
        new() { Id = fid + 100, Fid = fid, Name = name, TypeCode = type, IsSearchable = searchable, IsFilterable = filterable };

    private static FilterGroup Tree(params FilterCondition[] conditions) =>
        new() { Logic = "and", Nodes = conditions.Select(c => new FilterNode { Condition = c }).ToList() };

    private static FilterCondition Cond(int fid, string op = "eq", string? value = "Active", string? valueMode = null, long? valueFieldId = null, string? subField = null) =>
        new() { FieldId = fid, Operator = op, Value = value, ValueMode = valueMode, ValueFieldId = valueFieldId, SubField = subField };

    private static PipelineAiSearchPlan Plan(FilterGroup? tree, params AppField[] fields) =>
        PipelineAiSearchPlanner.Evaluate(tree, fields.Length == 0 ? [Field(7)] : fields);

    // ── what is answered by the index ───────────────────────────────────────────────────────

    [Fact]
    public void Equality_OnASearchableFilterableTextField_UsesAPhraseMatchOnItsColumn()
    {
        var plan = Plan(Tree(Cond(7)));

        Assert.True(plan.UseAiSearch);
        Assert.Null(plan.SqlReason);
        Assert.Equal("search.ismatch('\"Active\"', 'f_7', 'full', 'any')", plan.ODataFilter);
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("TextMultiLine")]
    [InlineData("RichText")]
    [InlineData("Email")]
    [InlineData("Phone")]
    [InlineData("Url")]
    [InlineData("SingleSelect")]
    public void PlainTextTypes_AreAnsweredByTheIndex(string type) =>
        Assert.True(Plan(Tree(Cond(7)), Field(7, type)).UseAiSearch);

    [Fact]
    public void InList_BecomesOneOrOfPhraseMatches()
    {
        var plan = Plan(Tree(Cond(7, "in", "[\"Active\",\"Pending\"]")));

        Assert.Equal("(search.ismatch('\"Active\"', 'f_7', 'full', 'any') or search.ismatch('\"Pending\"', 'f_7', 'full', 'any'))", plan.ODataFilter);
    }

    [Fact]
    public void InList_AsCommaSeparatedText_IsReadTheSameWayTheSqlFilterReadsIt()
    {
        var plan = Plan(Tree(Cond(7, "in", "Active, Pending")));

        Assert.Equal("(search.ismatch('\"Active\"', 'f_7', 'full', 'any') or search.ismatch('\"Pending\"', 'f_7', 'full', 'any'))", plan.ODataFilter);
    }

    [Fact]
    public void InList_WithOneValue_IsAPlainPhraseMatch() =>
        Assert.Equal("search.ismatch('\"Active\"', 'f_7', 'full', 'any')", Plan(Tree(Cond(7, "in", "[\"Active\"]"))).ODataFilter);

    [Fact]
    public void QuotesBackslashesAndApostrophesInTheValue_AreEscapedForLuceneAndForTheODataString()
    {
        var plan = Plan(Tree(Cond(7, "eq", "O'Brien \"Jr\" \\ team")));

        Assert.Equal("search.ismatch('\"O''Brien \\\"Jr\\\" \\\\ team\"', 'f_7', 'full', 'any')", plan.ODataFilter);
    }

    [Fact]
    public void NonLatinText_IsEligible()
    {
        var plan = Plan(Tree(Cond(7, "eq", "ગુજરાતી")));

        Assert.True(plan.UseAiSearch);
        Assert.Contains("ગુજરાતી", plan.ODataFilter);
    }

    [Fact]
    public void AndOfSeveralEligibleConditions_AreJoinedWithAnd()
    {
        var plan = Plan(Tree(Cond(7, value: "Active"), Cond(8, value: "North")), Field(7), Field(8, name: "Region"));

        Assert.Equal("search.ismatch('\"Active\"', 'f_7', 'full', 'any') and search.ismatch('\"North\"', 'f_8', 'full', 'any')", plan.ODataFilter);
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

        var plan = Plan(tree, Field(7), Field(8, name: "Region"));

        Assert.Equal("search.ismatch('\"Active\"', 'f_7', 'full', 'any') and (search.ismatch('\"North\"', 'f_8', 'full', 'any') or search.ismatch('\"South\"', 'f_8', 'full', 'any'))", plan.ODataFilter);
    }

    [Fact]
    public void EmptyNestedGroups_AreIgnored()
    {
        var tree = new FilterGroup
        {
            Nodes = [new() { Group = new FilterGroup() }, new() { Condition = Cond(7) }]
        };

        Assert.Equal("search.ismatch('\"Active\"', 'f_7', 'full', 'any')", Plan(tree).ODataFilter);
    }

    [Fact]
    public void LiteralValueMode_IsAccepted() =>
        Assert.True(Plan(Tree(Cond(7, valueMode: "literal"))).UseAiSearch);

    [Fact]
    public void FieldMetadataCaseIsHonouredPerCondition()
    {
        // The field list may hold the same fid only once; a duplicate must not throw.
        Assert.True(Plan(Tree(Cond(7)), Field(7), Field(7)).UseAiSearch);
    }

    // ── what must be read from SQL ──────────────────────────────────────────────────────────

    [Fact]
    public void NoFilter_IsReadFromSql()
    {
        Assert.False(Plan(null).UseAiSearch);
        Assert.False(Plan(new FilterGroup()).UseAiSearch);
        Assert.False(Plan(new FilterGroup { Nodes = [new() { Group = new FilterGroup() }] }).UseAiSearch);
    }

    [Theory]
    [InlineData("Formula")]
    [InlineData("Formula_Text")]
    [InlineData("Formula_Number")]
    [InlineData("Lookup")]
    [InlineData("Summary")]
    [InlineData("ReportLink")]
    [InlineData("ActionButton")]
    [InlineData("ActionButton_Data")]
    [InlineData("Reference")]
    public void ComputedAndRelationshipFields_AreReadFromSql(string type)
    {
        var plan = Plan(Tree(Cond(7)), Field(7, type));

        Assert.False(plan.UseAiSearch);
        Assert.Contains(type, plan.SqlReason);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void AFieldThatIsNotBothSearchableAndFilterable_IsReadFromSql(bool searchable, bool filterable)
    {
        var plan = Plan(Tree(Cond(7)), Field(7, searchable: searchable, filterable: filterable));

        Assert.False(plan.UseAiSearch);
        Assert.Contains("not both searchable and filterable", plan.SqlReason);
    }

    [Theory]
    [InlineData("Number")]
    [InlineData("Currency")]
    [InlineData("Percent")]
    [InlineData("Rating")]
    [InlineData("Date")]
    [InlineData("DateTime")]
    [InlineData("Time")]
    [InlineData("Duration")]
    [InlineData("Boolean")]
    [InlineData("User")]
    [InlineData("MultiUser")]
    [InlineData("MultiSelect")]
    [InlineData("Address")]
    [InlineData("File")]
    [InlineData("DateRange")]
    [InlineData("NumericRange")]
    public void NonTextTypes_AreReadFromSql(string type) =>
        Assert.False(Plan(Tree(Cond(7)), Field(7, type)).UseAiSearch);

    [Theory]
    [InlineData("ne")]
    [InlineData("notIn")]
    [InlineData("contains")]
    [InlineData("notContains")]
    [InlineData("startsWith")]
    [InlineData("notStartsWith")]
    [InlineData("isEmpty")]
    [InlineData("isNotEmpty")]
    [InlineData("gt")]
    [InlineData("gte")]
    [InlineData("lt")]
    [InlineData("lte")]
    [InlineData("wildcard")]
    [InlineData("EQ")]       // operator names are exact; an unknown spelling is not guessed at
    [InlineData("")]
    public void OtherOperators_AreReadFromSql(string op)
    {
        var plan = Plan(Tree(Cond(7, op)));

        Assert.False(plan.UseAiSearch);
        Assert.NotNull(plan.SqlReason);
    }

    [Fact]
    public void UnknownField_IsReadFromSql() =>
        Assert.False(Plan(Tree(Cond(99)), Field(7)).UseAiSearch);

    [Fact]
    public void ConditionOnASubField_IsReadFromSql() =>
        Assert.False(Plan(Tree(Cond(7, subField: "city"))).UseAiSearch);

    [Theory]
    [InlineData("field", null)]
    [InlineData("field", 8L)]
    [InlineData("parentField", 8L)]
    [InlineData("ask", null)]
    [InlineData(null, 8L)]
    public void ConditionThatIsNotALiteral_IsReadFromSql(string? mode, long? valueFieldId) =>
        Assert.False(Plan(Tree(Cond(7, valueMode: mode, valueFieldId: valueFieldId))).UseAiSearch);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyValue_IsReadFromSql(string? value)
    {
        // The SQL filter treats an empty value as "no condition", which the index cannot mirror.
        Assert.False(Plan(Tree(Cond(7, value: value))).UseAiSearch);
    }

    [Theory]
    [InlineData("the")]                  // only stop words: the index drops them from its text
    [InlineData("a and the")]
    [InlineData("---")]                  // nothing a word could match
    [InlineData("!!!")]
    public void ValueTheIndexCannotMatch_IsReadFromSql(string value) =>
        Assert.False(Plan(Tree(Cond(7, value: value))).UseAiSearch);

    [Theory]
    [InlineData("the matrix")]           // a stop word does not stop the other words from matching
    [InlineData("ORD0002694177")]
    [InlineData("a-1")]
    [InlineData("42")]
    public void ValueWithAWordTheIndexKeeps_IsEligible(string value) =>
        Assert.True(Plan(Tree(Cond(7, value: value))).UseAiSearch);

    [Fact]
    public void VeryLongValue_IsReadFromSql() =>
        Assert.False(Plan(Tree(Cond(7, value: new string('x', PipelineAiSearchPlanner.MaxValueLength + 1)))).UseAiSearch);

    [Fact]
    public void ValueAtTheLengthLimit_IsEligible() =>
        Assert.True(Plan(Tree(Cond(7, value: new string('x', PipelineAiSearchPlanner.MaxValueLength)))).UseAiSearch);

    [Fact]
    public void TooLongAnInList_IsReadFromSql()
    {
        var many = JsonSerializer.Serialize(Enumerable.Range(0, PipelineAiSearchPlanner.MaxInValues + 1).Select(i => $"value{i}"));
        var atLimit = JsonSerializer.Serialize(Enumerable.Range(0, PipelineAiSearchPlanner.MaxInValues).Select(i => $"value{i}"));

        Assert.False(Plan(Tree(Cond(7, "in", many))).UseAiSearch);
        Assert.True(Plan(Tree(Cond(7, "in", atLimit))).UseAiSearch);
    }

    [Fact]
    public void InListLimit_IsForTheWholeFilter_NotPerCondition()
    {
        var half = PipelineAiSearchPlanner.MaxInValues / 2;
        var first = JsonSerializer.Serialize(Enumerable.Range(0, half).Select(i => $"value{i}"));
        var second = JsonSerializer.Serialize(Enumerable.Range(0, half).Select(i => $"other{i}"));
        var secondOneMore = JsonSerializer.Serialize(Enumerable.Range(0, half + 1).Select(i => $"other{i}"));

        // Two lists that together reach the limit are fine; one more value makes the filter's lists too long, though neither alone is.
        Assert.True(Plan(Tree(Cond(7, "in", first), Cond(7, "in", second))).UseAiSearch);
        var over = Plan(Tree(Cond(7, "in", first), Cond(7, "in", secondOneMore)));
        Assert.False(over.UseAiSearch);
        Assert.Contains("longer than", over.SqlReason);
    }

    [Fact]
    public void FilterStaysWithinOneHundredClauses()
    {
        // Azure AI Search warns that hundreds of clauses risk its limit; every condition and every list value is one clause.
        Assert.True(PipelineAiSearchPlanner.MaxInValues + PipelineAiSearchPlanner.MaxConditions <= 100);
    }

    [Fact]
    public void TooManyConditions_AreReadFromSql()
    {
        var atLimit = Tree(Enumerable.Range(0, PipelineAiSearchPlanner.MaxConditions).Select(i => Cond(7, value: $"v{i}")).ToArray());
        var over = Tree(Enumerable.Range(0, PipelineAiSearchPlanner.MaxConditions + 1).Select(i => Cond(7, value: $"v{i}")).ToArray());

        Assert.True(Plan(atLimit).UseAiSearch);
        var plan = Plan(over);
        Assert.False(plan.UseAiSearch);
        Assert.Contains("more than", plan.SqlReason);
    }

    [Fact]
    public void WorstCaseSqlParameters_StayUnderSqlServersLimit()
    {
        // Verifying a page of candidates sends their record ids AND every value of the search's own filter as parameters
        // (plus a handful for paging, the snapshot bound and the owner restriction). The limit is 2,100.
        const int handful = 20;
        var worst = PipelineEngine.AiCandidateRecordIdChunkSize + PipelineAiSearchPlanner.MaxInValues + PipelineAiSearchPlanner.MaxConditions + handful;

        Assert.True(worst < 2100, $"{worst} parameters in the worst case");
        Assert.True(PipelineEngine.AiCandidatePublicIdChunkSize < 2100);   // the bare IN (candidate public ids) has no other parameters
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("[\"the\",\"Active\"]")]   // one value the index cannot match makes the whole list unanswerable
    public void InListThatCannotBeAnsweredExactly_IsReadFromSql(string value) =>
        Assert.False(Plan(Tree(Cond(7, "in", value))).UseAiSearch);

    [Fact]
    public void OneIneligibleConditionAmongEligibleOnes_SendsTheWholeSearchToSql()
    {
        var fields = new[] { Field(7), Field(8, "Formula_Text", name: "Order no") };

        var plan = Plan(Tree(Cond(7), Cond(8)), fields);

        Assert.False(plan.UseAiSearch);
        Assert.Contains("Order no", plan.SqlReason);
    }

    [Fact]
    public void OneIneligibleConditionInsideANestedOrGroup_SendsTheWholeSearchToSql()
    {
        var tree = new FilterGroup
        {
            Nodes =
            [
                new() { Condition = Cond(7) },
                new() { Group = new FilterGroup { Logic = "or", Nodes = [new() { Condition = Cond(8) }, new() { Condition = Cond(9, "gt", "5") }] } }
            ]
        };

        Assert.False(Plan(tree, Field(7), Field(8), Field(9, "Number")).UseAiSearch);
    }

    [Fact]
    public void OnlyTheFieldsOfTheConditionsMatter()
    {
        // A table that also has formula and number fields is still eligible when the filter does not touch them.
        var fields = new[] { Field(7), Field(8, "Formula_Number"), Field(9, "Number"), Field(10, "Summary") };

        Assert.True(Plan(Tree(Cond(7)), fields).UseAiSearch);
    }

    [Fact]
    public void Evaluate_DoesNotModifyTheFilterOrTheFields()
    {
        var tree = Tree(Cond(7, "in", "[\"a1\",\"b2\"]"));
        var before = JsonSerializer.Serialize(tree);
        var fields = new[] { Field(7) };

        PipelineAiSearchPlanner.Evaluate(tree, fields);

        Assert.Equal(before, JsonSerializer.Serialize(tree));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Active", true)]
    [InlineData("the", false)]
    [InlineData("THE", false)]           // stop words are matched without regard to case
    [InlineData("to be or not to be", false)]
    [InlineData("to be Hamlet", true)]
    [InlineData("a_b", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("é", true)]
    public void IsMatchable_NeedsAWordTheIndexKeeps(string? value, bool expected) =>
        Assert.Equal(expected, PipelineAiSearchPlanner.IsMatchable(value));

    [Theory]
    [InlineData("[\"a\",\"b\"]", new[] { "a", "b" })]
    [InlineData("a,b", new[] { "a", "b" })]
    [InlineData("a, b ,c", new[] { "a", "b", "c" })]
    [InlineData("[1,2]", new[] { "1", "2" })]
    [InlineData("[\"a\",\"\",\" \"]", new[] { "a" })]
    [InlineData("", new string[0])]
    public void ParseValueList_ReadsJsonOrCommaSeparatedLists(string raw, string[] expected) =>
        Assert.Equal(expected, PipelineAiSearchPlanner.ParseValueList(raw));

    [Fact]
    public void Phrase_QuotesTheTextAsALuceneProximityFreePhrase() =>
        Assert.Equal("search.ismatch('\"a b\"', 'f_3', 'full', 'any')", PipelineAiSearchPlanner.Phrase("f_3", "a b"));
}
