using System;
using System.Collections.Generic;
using FluentAssertions;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport.OData;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Reports;

public class ODataFilterBuilderTests
{
    private readonly AppField _textField = new()
    {
        Id = 1,
        Fid = 1,
        Name = "Title",
        TypeCode = "Text",
        IsSearchable = true,
        IsFilterable = true
    };

    private readonly AppField _numberField = new()
    {
        Id = 2,
        Fid = 2,
        Name = "Amount",
        TypeCode = "Number",
        IsSearchable = true,
        IsFilterable = true
    };

    private readonly AppField _dateField = new()
    {
        Id = 3,
        Fid = 3,
        Name = "DueDate",
        TypeCode = "Date",
        IsSearchable = true,
        IsFilterable = true
    };

    private readonly AppField _multiSelectField = new()
    {
        Id = 4,
        Fid = 4,
        Name = "Tags",
        TypeCode = "MultiSelect",
        IsSearchable = true,
        IsFilterable = true
    };

    private readonly AppField _formulaField = new()
    {
        Id = 5,
        Fid = 5,
        Name = "TotalFormula",
        TypeCode = "Formula",
        IsSearchable = false,
        IsFilterable = false
    };

    private readonly AppField _unindexedField = new()
    {
        Id = 6,
        Fid = 6,
        Name = "InternalNotes",
        TypeCode = "Text",
        IsSearchable = false,
        IsFilterable = false
    };

    [Fact]
    public void Build_DateEq_Generates24HourBoundingBoxInUtc()
    {
        var fields = new[] { _dateField };
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 3, Operator = "date_eq", Value = "2026-09-08" } }]
        };

        var result = ODataFilterBuilder.Build(tree, fields);

        result.Should().NotBeNull();
        result.Should().Contain("f_3 ge '2026-09-08T00:00:00Z'");
        result.Should().Contain("f_3 lt '2026-09-09T00:00:00Z'");
    }

    [Fact]
    public void Build_NumericRange_ReturnsNull_ToDelegateToSql()
    {
        var fields = new[] { _numberField };
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 2, Operator = "gt", Value = "100" } }]
        };

        var result = ODataFilterBuilder.Build(tree, fields);

        // Numeric range comparisons should return null so that string comparison does not drop records
        result.Should().BeNull();
    }

    [Fact]
    public void Build_NumericEquality_GeneratesExactMatch()
    {
        var fields = new[] { _numberField };
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 2, Operator = "eq", Value = "100" } }]
        };

        var result = ODataFilterBuilder.Build(tree, fields);

        result.Should().Be("f_2 eq '100'");
    }

    [Fact]
    public void Build_IncludesAndNotIncludes_GeneratesPhraseQuery()
    {
        var fields = new[] { _multiSelectField };
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new FilterNode { Condition = new FilterCondition { FieldId = 4, Operator = "includes", Value = "Urgent" } },
                new FilterNode { Condition = new FilterCondition { FieldId = 4, Operator = "notIncludes", Value = "Archived" } }
            ]
        };

        var result = ODataFilterBuilder.Build(tree, fields);

        result.Should().Contain("search.ismatch('\"urgent\"', 'f_4')");
        result.Should().Contain("not search.ismatch('\"archived\"', 'f_4')");
    }

    [Fact]
    public void Build_FieldToFieldComparison_ReturnsNull_ToDelegateToSql()
    {
        var fields = new[] { _textField };
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 1, Operator = "eq", ValueMode = "field", ValueFieldId = 2 } }]
        };

        var result = ODataFilterBuilder.Build(tree, fields);

        result.Should().BeNull();
    }

    [Fact]
    public void Build_OrGroup_WithOneUnindexedBranch_ReturnsNull_ToPreventDroppingValidRecords()
    {
        var fields = new[] { _textField, _formulaField };
        var tree = new FilterGroup
        {
            Logic = "or",
            Nodes =
            [
                new FilterNode { Condition = new FilterCondition { FieldId = 1, Operator = "eq", Value = "Open" } },
                new FilterNode { Condition = new FilterCondition { FieldId = 5, Operator = "eq", Value = "Calculated" } } // Formula
            ]
        };

        var result = ODataFilterBuilder.Build(tree, fields);

        // Entire OR group must be null so that Azure doesn't partially evaluate branch 1 and drop branch 2
        result.Should().BeNull();
    }

    [Fact]
    public void Build_OrGroup_WithAllIndexableBranches_GeneratesOrOData()
    {
        var fields = new[] { _textField, _multiSelectField };
        var tree = new FilterGroup
        {
            Logic = "or",
            Nodes =
            [
                new FilterNode { Condition = new FilterCondition { FieldId = 1, Operator = "eq", Value = "Open" } },
                new FilterNode { Condition = new FilterCondition { FieldId = 1, Operator = "eq", Value = "Pending" } }
            ]
        };

        var result = ODataFilterBuilder.Build(tree, fields);

        result.Should().Be("f_1 eq 'Open' or f_1 eq 'Pending'");
    }

    [Fact]
    public void Build_AndGroup_WithOneUnindexedBranch_ExtractsIndexableBranch()
    {
        var fields = new[] { _textField, _formulaField };
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new FilterNode { Condition = new FilterCondition { FieldId = 1, Operator = "eq", Value = "Open" } },
                new FilterNode { Condition = new FilterCondition { FieldId = 5, Operator = "eq", Value = "Calculated" } } // Formula
            ]
        };

        var result = ODataFilterBuilder.Build(tree, fields);

        // In an AND group, pre-filtering by the indexable branch is safe and narrows candidate records
        result.Should().Be("f_1 eq 'Open'");
    }
}
