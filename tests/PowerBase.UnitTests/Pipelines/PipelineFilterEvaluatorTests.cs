using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineFilterEvaluatorTests
{
    private readonly List<AppField> _fields;

    public PipelineFilterEvaluatorTests()
    {
        _fields = new List<AppField>
        {
            new() { Id = 1, Fid = 1, Name = "Name", TypeCode = "TEXT" },
            new() { Id = 2, Fid = 2, Name = "Price", TypeCode = "NUMBER" },
            new() { Id = 3, Fid = 3, Name = "Category", TypeCode = "TEXT" },
            new() { Id = 4, Fid = 4, Name = "Active", TypeCode = "BOOLEAN" }
        };
    }

    [Fact]
    public void EvaluateGroup_SingleGroup_AND_TruthTable()
    {
        // Case 1: T AND T -> T
        var groupTT = new TriggerFilterGroup
        {
            LogicalOp = "OR", // default UI value
            Rules = new List<TriggerFilterRule>
            {
                new() { Field = "Name", Operator = "contains", Value = "Ronak" },
                new() { Field = "Price", Operator = "greater_than", Value = "5" }
            }
        };
        var sourceTT = new Dictionary<long, object?> { [1] = "Ronak", [2] = 10 };
        PipelineFilterEvaluator.EvaluateGroup(groupTT, sourceTT, _fields).Should().BeTrue();

        // Case 2: T AND F -> F
        var sourceTF = new Dictionary<long, object?> { [1] = "Ronak", [2] = 3 };
        PipelineFilterEvaluator.EvaluateGroup(groupTT, sourceTF, _fields).Should().BeFalse();

        // Case 3: F AND T -> F
        var sourceFT = new Dictionary<long, object?> { [1] = "Ashish", [2] = 10 };
        PipelineFilterEvaluator.EvaluateGroup(groupTT, sourceFT, _fields).Should().BeFalse();

        // Case 4: F AND F -> F
        var sourceFF = new Dictionary<long, object?> { [1] = "Ashish", [2] = 3 };
        PipelineFilterEvaluator.EvaluateGroup(groupTT, sourceFF, _fields).Should().BeFalse();
    }

    [Fact]
    public void EvaluateGroup_MultipleGroups_OR_TruthTable()
    {
        // Group 1: Name contains "Ronak"
        var g1 = new TriggerFilterGroup
        {
            LogicalOp = "OR",
            Rules = new List<TriggerFilterRule> { new() { Field = "Name", Operator = "contains", Value = "Ronak" } }
        };

        // Group 2: Price > 5
        var g2 = new TriggerFilterGroup
        {
            LogicalOp = "OR",
            Rules = new List<TriggerFilterRule> { new() { Field = "Price", Operator = "greater_than", Value = "5" } }
        };

        var groups = new List<TriggerFilterGroup> { g1, g2 };

        // T OR T -> T
        var sourceTT = new Dictionary<long, object?> { [1] = "Ronak", [2] = 10 };
        groups.Any(g => PipelineFilterEvaluator.EvaluateGroup(g, sourceTT, _fields)).Should().BeTrue();

        // T OR F -> T
        var sourceTF = new Dictionary<long, object?> { [1] = "Ronak", [2] = 3 };
        groups.Any(g => PipelineFilterEvaluator.EvaluateGroup(g, sourceTF, _fields)).Should().BeTrue();

        // F OR T -> T
        var sourceFT = new Dictionary<long, object?> { [1] = "Ashish", [2] = 10 };
        groups.Any(g => PipelineFilterEvaluator.EvaluateGroup(g, sourceFT, _fields)).Should().BeTrue();

        // F OR F -> F
        var sourceFF = new Dictionary<long, object?> { [1] = "Ashish", [2] = 3 };
        groups.Any(g => PipelineFilterEvaluator.EvaluateGroup(g, sourceFF, _fields)).Should().BeFalse();
    }

    [Fact]
    public void EvaluateGroup_ExactRegressionCase()
    {
        // Record: Name = Ronak, Price = 3
        var source = new Dictionary<long, object?> { [1] = "Ronak", [2] = 3 };

        // Filter: Name contains "Ronak" AND Price > 5
        var group = new TriggerFilterGroup
        {
            LogicalOp = "OR", // saved default
            Rules = new List<TriggerFilterRule>
            {
                new() { Field = "Name", Operator = "contains", Value = "Ronak" },
                new() { Field = "Price", Operator = "greater_than", Value = "5" }
            }
        };

        // Evaluation
        bool result = PipelineFilterEvaluator.EvaluateGroup(group, source, _fields);

        // Expect FALSE (no trigger)
        result.Should().BeFalse();
    }

    [Fact]
    public void EvaluateGroup_PositiveCase()
    {
        // Record: Name = Ronak, Price = 10
        var source = new Dictionary<long, object?> { [1] = "Ronak", [2] = 10 };

        // Filter: Name contains "Ronak" AND Price > 5
        var group = new TriggerFilterGroup
        {
            LogicalOp = "OR",
            Rules = new List<TriggerFilterRule>
            {
                new() { Field = "Name", Operator = "contains", Value = "Ronak" },
                new() { Field = "Price", Operator = "greater_than", Value = "5" }
            }
        };

        // Evaluation
        bool result = PipelineFilterEvaluator.EvaluateGroup(group, source, _fields);

        // Expect TRUE
        result.Should().BeTrue();
    }

    [Fact]
    public void IsRuleCompletelyBlank_PlaceholderRows_AreIgnored()
    {
        var blankRule = new TriggerFilterRule { Field = "", Operator = "is", Value = "" };
        PipelineFilterEvaluator.IsRuleCompletelyBlank(blankRule).Should().BeTrue();

        var normalRule = new TriggerFilterRule { Field = "Name", Operator = "contains", Value = "Ronak" };
        PipelineFilterEvaluator.IsRuleCompletelyBlank(normalRule).Should().BeFalse();
    }

    [Fact]
    public void EvaluateGroup_NestedGroups_Logic()
    {
        // Filter: Name contains "Ronak" AND (Price > 5 OR Category = "VIP")
        // Represented as:
        // Group containing:
        //   - Rule A: Name contains "Ronak"
        //   - Rule B: Nested group with:
        //       - Sub-group 1: Price > 5
        //       - Sub-group 2: Category = "VIP"
        var group = new TriggerFilterGroup
        {
            LogicalOp = "OR",
            Rules = new List<TriggerFilterRule>
            {
                new() { Field = "Name", Operator = "contains", Value = "Ronak" },
                new()
                {
                    Type = "nested",
                    Groups = new List<TriggerFilterGroup>
                    {
                        new() { LogicalOp = "OR", Rules = new List<TriggerFilterRule> { new() { Field = "Price", Operator = "greater_than", Value = "5" } } },
                        new() { LogicalOp = "OR", Rules = new List<TriggerFilterRule> { new() { Field = "Category", Operator = "equals", Value = "VIP" } } }
                    }
                }
            }
        };

        // Case 1: Ronak, Price 10, Normal -> TRUE
        var source1 = new Dictionary<long, object?> { [1] = "Ronak", [2] = 10, [3] = "Normal" };
        PipelineFilterEvaluator.EvaluateGroup(group, source1, _fields).Should().BeTrue();

        // Case 2: Ronak, Price 3, VIP -> TRUE
        var source2 = new Dictionary<long, object?> { [1] = "Ronak", [2] = 3, [3] = "VIP" };
        PipelineFilterEvaluator.EvaluateGroup(group, source2, _fields).Should().BeTrue();

        // Case 3: Ronak, Price 3, Normal -> FALSE
        var source3 = new Dictionary<long, object?> { [1] = "Ronak", [2] = 3, [3] = "Normal" };
        PipelineFilterEvaluator.EvaluateGroup(group, source3, _fields).Should().BeFalse();

        // Case 4: Ashish, Price 10, VIP -> FALSE
        var source4 = new Dictionary<long, object?> { [1] = "Ashish", [2] = 10, [3] = "VIP" };
        PipelineFilterEvaluator.EvaluateGroup(group, source4, _fields).Should().BeFalse();
    }

    [Theory]
    [InlineData("not-equals", "Active", "Inactive", true)]
    [InlineData("not_equals", "Active", "Inactive", true)]
    [InlineData("greater-than", "10", "5", true)]
    [InlineData("less-than", "3", "5", true)]
    [InlineData("starts-with", "Hello", "He", true)]
    [InlineData("is-null", "", "", true)]
    [InlineData("is-not-null", "value", "", true)]
    public void EvaluateConditionOperator_HyphenatedConditionUiOperators_EvaluateCorrectly(
        string op, string left, string right, bool expected)
    {
        PipelineFilterEvaluator.EvaluateConditionOperator(left, op, right)
            .Should().Be(expected);
    }

    [Fact]
    public void EvaluateGroup_MultipleTopLevelGroups()
    {
        // Filter: (Name contains "Ronak" AND Price > 5) OR (Category = "VIP" AND Active = true)
        var g1 = new TriggerFilterGroup
        {
            LogicalOp = "OR",
            Rules = new List<TriggerFilterRule>
            {
                new() { Field = "Name", Operator = "contains", Value = "Ronak" },
                new() { Field = "Price", Operator = "greater_than", Value = "5" }
            }
        };

        var g2 = new TriggerFilterGroup
        {
            LogicalOp = "OR",
            Rules = new List<TriggerFilterRule>
            {
                new() { Field = "Category", Operator = "equals", Value = "VIP" },
                new() { Field = "Active", Operator = "is_true", Value = "" }
            }
        };

        var groups = new List<TriggerFilterGroup> { g1, g2 };

        // Group 1 true, Group 2 false -> TRUE
        var source1 = new Dictionary<long, object?> { [1] = "Ronak", [2] = 10, [3] = "Normal", [4] = "false" };
        groups.Any(g => PipelineFilterEvaluator.EvaluateGroup(g, source1, _fields)).Should().BeTrue();

        // Group 1 false, Group 2 true -> TRUE
        var source2 = new Dictionary<long, object?> { [1] = "Ashish", [2] = 3, [3] = "VIP", [4] = "true" };
        groups.Any(g => PipelineFilterEvaluator.EvaluateGroup(g, source2, _fields)).Should().BeTrue();

        // Both false -> FALSE
        var source3 = new Dictionary<long, object?> { [1] = "Ashish", [2] = 3, [3] = "Normal", [4] = "false" };
        groups.Any(g => PipelineFilterEvaluator.EvaluateGroup(g, source3, _fields)).Should().BeFalse();

        // Both true -> TRUE
        var source4 = new Dictionary<long, object?> { [1] = "Ronak", [2] = 10, [3] = "VIP", [4] = "true" };
        groups.Any(g => PipelineFilterEvaluator.EvaluateGroup(g, source4, _fields)).Should().BeTrue();
    }

    [Theory]
    // Configured Right Date: 2026-09-07T00:00:00.000Z
    // 1. Sep 6 23:59
    [InlineData("2026-09-06T23:59:59Z", "is", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-06T23:59:59Z", "is-not", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-06T23:59:59Z", "after", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-06T23:59:59Z", "on-or-after", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-06T23:59:59Z", "before", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-06T23:59:59Z", "on-or-before", "2026-09-07T00:00:00Z", true)]
    // 2. Sep 7 00:00
    [InlineData("2026-09-07T00:00:00Z", "is", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-07T00:00:00Z", "is-not", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T00:00:00Z", "after", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T00:00:00Z", "on-or-after", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-07T00:00:00Z", "before", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T00:00:00Z", "on-or-before", "2026-09-07T00:00:00Z", true)]
    // 3. Sep 7 16:18 (Critical same-day timestamp test!)
    [InlineData("2026-09-07T16:18:00Z", "is", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-07T16:18:00Z", "is-not", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T16:18:00Z", "after", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T16:18:00Z", "on-or-after", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-07T16:18:00Z", "before", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T16:18:00Z", "on-or-before", "2026-09-07T00:00:00Z", true)]
    // 4. Sep 7 23:59
    [InlineData("2026-09-07T23:59:59Z", "is", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-07T23:59:59Z", "is-not", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T23:59:59Z", "after", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T23:59:59Z", "on-or-after", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-07T23:59:59Z", "before", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-07T23:59:59Z", "on-or-before", "2026-09-07T00:00:00Z", true)]
    // 5. Sep 8 00:00
    [InlineData("2026-09-08T00:00:00Z", "is", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-08T00:00:00Z", "is-not", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-08T00:00:00Z", "after", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-08T00:00:00Z", "on-or-after", "2026-09-07T00:00:00Z", true)]
    [InlineData("2026-09-08T00:00:00Z", "before", "2026-09-07T00:00:00Z", false)]
    [InlineData("2026-09-08T00:00:00Z", "on-or-before", "2026-09-07T00:00:00Z", false)]
    public void EvaluateConditionOperator_DateOnlyOperators_CompleteTruthTable(
        string left, string op, string right, bool expected)
    {
        PipelineFilterEvaluator.EvaluateConditionOperator(left, op, right, typeCategory: "DATE")
            .Should().Be(expected);
    }

    [Fact]
    public void EvaluateConditionOperator_DynamicDateOperands_SameDayDifferentTime_EvaluatesDateOnly()
    {
        // Left: 23:59 on Sept 7, Right: 00:01 on Sept 7
        string left = "2026-09-07T23:59:00Z";
        string right = "2026-09-07T00:01:00Z";

        // AFTER should be false because calendar dates are identical
        PipelineFilterEvaluator.EvaluateConditionOperator(left, "after", right, typeCategory: "DATE")
            .Should().BeFalse();

        // IS should be true because calendar dates are identical
        PipelineFilterEvaluator.EvaluateConditionOperator(left, "is", right, typeCategory: "DATE")
            .Should().BeTrue();
    }
}

