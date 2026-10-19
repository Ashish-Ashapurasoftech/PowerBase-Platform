using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>Several "does not contain" conditions on one field: joined by "any" (OR) every row passes, because a row can contain at most one of the names and so fails
/// to contain another; joined by "all" (AND) only a row containing none of them passes. The engine follows the logic it is given.</summary>
public class ImportRowFilterLogicTests
{
    private static readonly AppField Project = new() { Id = 1, AppTableId = 0, Fid = 1001, Name = "Project Name", TypeCode = "Text" };

    private static FilterGroup Group(string logic, params string[] names) => new()
    {
        Logic = logic,
        Nodes = names.Select(n => new FilterNode { Condition = new FilterCondition { FieldId = 1001, Operator = "notContains", Value = n } }).ToList()
    };

    private static bool Passes(FilterGroup group, string project) =>
        new ImportRowFilter(group, [Project]).Matches(new Dictionary<string, object?> { [PowerBase.Domain.Constants.PhysicalNaming.GetPhysicalColumnName(Project)] = project });

    [Theory]
    [InlineData("Data Warehouse - 00001")]
    [InlineData("Marketing Automation - 00002")]
    [InlineData("Some other project")]
    public void Any_of_several_does_not_contain_conditions_lets_every_row_through(string project) =>
        Passes(Group("or", "Data Warehouse - 00001", "Marketing Automation - 00002"), project).Should().BeTrue();

    [Theory]
    [InlineData("Data Warehouse - 00001", false)]
    [InlineData("Marketing Automation - 00002", false)]
    [InlineData("Some other project", true)]
    public void All_of_several_does_not_contain_conditions_leaves_out_every_listed_project(string project, bool imported) =>
        Passes(Group("and", "Data Warehouse - 00001", "Marketing Automation - 00002"), project).Should().Be(imported);
}
