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
    ];

    // Parent fields: Start Date (Date, 6), End Date (Date, 7), Budget Left (formula, 8).
    private static readonly List<AppField> ParentFields =
    [
        new() { Fid = 6, Name = "Start Date", TypeCode = "Date" },
        new() { Fid = 7, Name = "End Date", TypeCode = "Date" },
        new() { Fid = 8, Name = "Budget Left", TypeCode = "Formula_Number" },
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

    [Fact]
    public void FindProblem_CalculatedParentField_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(3, "lt", 8)), ChildFields, ParentFields)
            .Should().Contain("'Budget Left' is calculated");

    [Fact]
    public void FindProblem_TextOperatorAgainstParentField_IsRefused() =>
        SummaryCriteriaValidator.FindProblem(Group("and", ParentCond(6, "contains", 6)), ChildFields, ParentFields)
            .Should().Contain("does not support comparing to another field");
}
