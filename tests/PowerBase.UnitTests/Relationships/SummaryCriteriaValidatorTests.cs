using FluentAssertions;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Relationships;

public class SummaryCriteriaValidatorTests
{
    // Child fields: Record ID# (system, fid 3), Title (Text, 6), Owner (User, 7), Days Left (formula, 8).
    private static readonly List<AppField> ChildFields =
    [
        new() { Fid = 3, Name = "Record ID#", TypeCode = "RecordId", IsSystem = true },
        new() { Fid = 6, Name = "Title", TypeCode = "Text" },
        new() { Fid = 7, Name = "Owner", TypeCode = "User" },
        new() { Fid = 8, Name = "Days Left", TypeCode = "Formula_Number" },
        new() { Fid = 9, Name = "Session Date", TypeCode = "Date" },
        new() { Fid = 10, Name = "Add Notes", TypeCode = "Formula_Url" },
    ];

    // Parent fields: Start Date (Date, 6), End Date (Date, 7), Budget Left (formula, 8), Region (lookup of text, 10),
    // Avg Days Left (summary, 11), Next Start (formula returning a time, 12), Term (lookup of a date range, 13),
    // Late Sessions (summary whose criteria compare to Avg Days Left, 14).
    private static readonly List<AppField> ParentFields =
    [
        new() { Fid = 6, Name = "Start Date", TypeCode = "Date" },
        new() { Fid = 7, Name = "End Date", TypeCode = "Date" },
        new() { Fid = 8, Name = "Budget Left", TypeCode = "Formula_Number" },
        new() { Fid = 10, Name = "Region", TypeCode = "Lookup", Settings = """{"sourceFid":5,"sourceTypeCode":"Text"}""" },
        new() { Fid = 11, Name = "Avg Days Left", TypeCode = "Summary", Settings = """{"function":"Avg","targetFid":8}""" },
        new() { Fid = 12, Name = "Next Start", TypeCode = "Formula_Time" },
        new() { Fid = 13, Name = "Term", TypeCode = "Lookup", Settings = """{"sourceFid":5,"sourceTypeCode":"DateRange"}""" },
        new()
        {
            Fid = 14, Name = "Late Sessions", TypeCode = "Summary",
            Settings = """{"function":"Count","filterTree":"{\"logic\":\"and\",\"nodes\":[{\"condition\":{\"fieldId\":8,\"operator\":\"gt\",\"valueMode\":\"parentField\",\"valueFieldId\":11}}]}"}""",
        },
        new() { Fid = 15, Name = "Add Sessions", TypeCode = "Formula_Url" },
        new() { Fid = 16, Name = "Sessions records", TypeCode = "ReportLink" },
        new() { Fid = 17, Name = "Region Link", TypeCode = "Lookup", Settings = """{"sourceFid":5,"sourceTypeCode":"Formula_Url"}""" },
    ];

    private static FilterNode ParentCond(long fieldId, string op, long? parentFid) =>
        new() { Condition = new FilterCondition { FieldId = fieldId, Operator = op, ValueMode = "parentField", ValueFieldId = parentFid } };

    private static FilterNode Cond(long fieldId, string op, string? value = "x", string? valueMode = null) =>
        new() { Condition = new FilterCondition { FieldId = fieldId, Operator = op, Value = value, ValueMode = valueMode } };

    private static FilterGroup Group(string logic, params FilterNode[] nodes) => new() { Logic = logic, Nodes = [.. nodes] };

    private static FilterNode Nested(string logic, params FilterNode[] nodes) => new() { Group = Group(logic, nodes) };

    [Fact]
    public void FindProblem_NoCriteria_IsFine() =>
        SummaryCriteriaValidator.FindProblem(null, ChildFields, ParentFields).Should().BeNull();

    [Fact]
    public void FindProblem_ThreeLevelsOfGroupsOverStoredAndSystemFields_IsFine() =>
        SummaryCriteriaValidator.FindProblem(
            Group("and", Cond(3, "gt", "10"), Nested("or", Cond(6, "contains"), Nested("and", Cond(6, "isEmpty", null)))),
            ChildFields, ParentFields).Should().BeNull();

    [Fact]
    public void FindProblem_GroupsNestedMoreThanThreeLevels_AreAllowed() =>
        SummaryCriteriaValidator.FindProblem(
            Group("and", Nested("or", Nested("and", Nested("or", Nested("and", Cond(6, "eq")))))),
            ChildFields, ParentFields).Should().BeNull();

    [Fact]
    public void FindProblem_FieldFromAnotherTable_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", Cond(99, "eq")), ChildFields, ParentFields).Should().Contain("Unknown field ID");

    [Fact]
    public void FindProblem_UnknownOperator_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", Cond(6, "sounds like")), ChildFields, ParentFields).Should().Contain("Invalid operator");

    [Fact]
    public void FindProblem_FormulaField_IsAllowed_JudgedInMemory() =>
        SummaryCriteriaValidator.FindProblem(Group("and", Cond(8, "gt", "3")), ChildFields, ParentFields).Should().BeNull();

    [Fact]
    public void FindProblem_FormulaThatCantBeCompared_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", Cond(91, "eq", "x")),
            [.. ChildFields, new AppField { Id = 91, Fid = 91, Name = "T", Label = "T", TypeCode = "Formula_Time" }], ParentFields)
            .Should().Contain("'T' is calculated");

    [Fact]
    public void FindProblem_LookupOfASummary_IsAllowed_JudgedInMemory()
    {
        // "Dept - Min of Salary" (fid 31) pulls the parent's "Min of Salary" summary down: worked out per record.
        var lookup = new AppField { Fid = 31, Name = "Dept - Min of Salary", TypeCode = "Lookup", Settings = """{"sourceFid":8,"sourceTypeCode":"Summary"}""" };
        var sources = new Dictionary<long, AppField> { [31] = new() { Fid = 8, Name = "Min of Salary", TypeCode = "Summary", Settings = """{"function":"Min","targetTypeCode":"Currency"}""" } };

        SummaryCriteriaValidator.FindProblem(Group("and", Cond(31, "gt", "1000")), [.. ChildFields, lookup], ParentFields, sources)
            .Should().BeNull();
    }

    [Fact]
    public void FindProblem_CurrentUser_IsFineAtAnyDepth() =>
        SummaryCriteriaValidator.FindProblem(Group("and", Nested("or", Cond(7, "isCurrentUser", null))), ChildFields, ParentFields)
            .Should().BeNull();

    [Fact]
    public void FindProblem_AskTheUser_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", Cond(6, "eq", null, "ask")), ChildFields, ParentFields).Should().Contain("ask the user");

    // ── "the value in the parent's field" ──

    [Fact]
    public void FindProblem_BetweenTwoParentDates_IsFine() =>
        SummaryCriteriaValidator.FindProblem(
            Group("and", ParentCond(9, "gte", 6), Nested("or", ParentCond(9, "lte", 7))),
            ChildFields, ParentFields).Should().BeNull();

    [Fact]
    public void FindProblem_ParentFieldThatDoesNotExist_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(9, "gte", 99)), ChildFields, ParentFields)
            .Should().Contain("valid valueFieldId on the parent table");

    [Fact]
    public void FindProblem_ParentFieldMissing_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(9, "gte", null)), ChildFields, ParentFields)
            .Should().Contain("valid valueFieldId on the parent table");

    [Theory]
    [InlineData(8L)]    // a number formula
    [InlineData(10L)]   // a lookup of a text field
    [InlineData(11L)]   // a summary (Avg → a number)
    public void FindProblem_CalculatedParentFieldWithAComparableValue_IsAllowed(long parentFid) =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(6, "eq", parentFid)), ChildFields, ParentFields)
            .Should().BeNull();

    [Fact]
    public void FindProblem_UrlFormula_IsRefused() =>
        // A link ("Add Notes") isn't a value to filter on.
        SummaryCriteriaValidator.FindProblem(Group("and", Cond(10, "contains", "tasks")), ChildFields, ParentFields)
            .Should().Contain("'Add Notes' is calculated");

    [Theory]
    [InlineData(12L, "'Next Start' is calculated")]   // a time formula has no comparable value
    [InlineData(13L, "'Term' is calculated")]         // a looked-up range has no single value
    [InlineData(15L, "'Add Sessions' is calculated")]       // a URL formula is a link, not a value
    [InlineData(16L, "'Sessions records' is calculated")]   // a report link has no value at all
    [InlineData(17L, "'Region Link' is calculated")]        // a lookup of a URL formula is a link too
    public void FindProblem_CalculatedParentFieldWithNoComparableValue_IsRefused(long parentFid, string expected) =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(6, "eq", parentFid)), ChildFields, ParentFields)
            .Should().Contain(expected);

    [Fact]
    public void FindProblem_ComparingToTheSummaryItself_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(8, "gt", 11)), ChildFields, ParentFields, summaryFid: 11)
            .Should().Contain("'Avg Days Left' depends on this summary");

    [Fact]
    public void FindProblem_ComparingToAParentSummaryWhoseCriteriaCompareToThisOne_IsRefused() =>
        // Late Sessions (14) compares to Avg Days Left (11): Avg Days Left can't then compare to Late Sessions.
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(8, "gt", 14)), ChildFields, ParentFields, summaryFid: 11)
            .Should().Contain("'Late Sessions' depends on this summary");

    [Fact]
    public void FindProblem_ComparingToAnotherSummary_WhileEditingAnUnrelatedOne_IsFine() =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(8, "gt", 14)), ChildFields, ParentFields, summaryFid: 99)
            .Should().BeNull();

    [Fact]
    public void FindProblem_TextOperatorAgainstParentField_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(6, "startsWith", 6)), ChildFields, ParentFields)
            .Should().Contain("does not support comparing to another field");

    // "Owner contains the user in the parent's Account Manager": the two fields hold a user in common.
    [Theory]
    [InlineData("contains")]
    [InlineData("notContains")]
    public void FindProblem_ContainsTheUserInAParentUserField_IsFine(string op) =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(7, op, 18)), ChildFields,
                [.. ParentFields, new AppField { Fid = 18, Name = "Account Manager", TypeCode = "User" }])
            .Should().BeNull();
}
