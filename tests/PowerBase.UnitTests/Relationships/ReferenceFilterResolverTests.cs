using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

public class ReferenceFilterResolverTests
{
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();

    private static readonly List<AppField> StatusFields =
    [
        new() { Id = 1, Fid = 6, Name = "Name", TypeCode = "Text" },
        new() { Id = 2, Fid = 7, Name = "Type", TypeCode = "Number" },
    ];

    private static readonly List<AppField> TaskFields =
    [
        new() { Id = 30, Fid = 12, Name = "TaskType", TypeCode = "Reference" },
        new() { Id = 31, Fid = 13, Name = "Task Code", TypeCode = "Formula_Text" },
    ];

    private static readonly AppTable Junction = new() { Id = 99, PublicId = Guid.NewGuid() };

    private static readonly List<AppField> JunctionFields =
    [
        new() { Id = 20, Fid = 6, Name = "StatusId", TypeCode = "Reference" },
        new() { Id = 21, Fid = 7, Name = "TaskTypeId", TypeCode = "Reference" },
    ];

    public ReferenceFilterResolverTests()
    {
        _tables.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(Junction);
        _fields.ListByTableAsync(99, Arg.Any<CancellationToken>()).Returns(JunctionFields);
    }

    private Task<IReadOnlyList<ReferenceFilterClause>> Build(ReferenceSettings? s, Dictionary<int, string?> values) =>
        ReferenceFilterResolver.BuildAsync(s, TaskFields, StatusFields, values, _tables, _fields, default);

    [Fact]
    public async Task NoConditions_ReturnsNoClauses()
    {
        (await Build(new ReferenceSettings(), new())).Should().BeEmpty();
        (await Build(null, new())).Should().BeEmpty();
    }

    [Fact]
    public async Task DirectCondition_ResolvesParentFieldAndFormValue()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 12, ParentFid = 7 }] };

        var clauses = await Build(settings, new() { [12] = " 5 " });

        clauses.Should().ContainSingle();
        clauses[0].ParentField!.Fid.Should().Be(7);
        clauses[0].Value.Should().Be("5");
        clauses[0].JunctionTable.Should().BeNull();
    }

    [Fact]
    public async Task JunctionCondition_ResolvesJunctionTableAndFields()
    {
        var settings = new ReferenceSettings
        {
            FilterConditions = [new() { FormFid = 12, JunctionTableId = 99, JunctionParentFid = 6, JunctionValueFid = 7 }],
        };

        var clauses = await Build(settings, new() { [12] = "3" });

        clauses.Should().ContainSingle();
        clauses[0].JunctionTable.Should().BeSameAs(Junction);
        clauses[0].JunctionParentField!.Name.Should().Be("StatusId");
        clauses[0].JunctionValueField!.Name.Should().Be("TaskTypeId");
        clauses[0].Value.Should().Be("3");
    }

    [Fact]
    public async Task MissingOrBlankFormValue_YieldsNullValue_SoNothingMatches()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 12, ParentFid = 7 }] };

        (await Build(settings, new() { [12] = null }))[0].Value.Should().BeNull();
        (await Build(settings, new() { [12] = "  " }))[0].Value.Should().BeNull();
    }


    [Fact]
    public async Task ControllingFieldNotSupplied_IsSkipped_SoNoFilterApplies()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 12, ParentFid = 7 }] };

        (await Build(settings, new())).Should().BeEmpty();
    }

    [Fact]
    public async Task ControllingFieldDeleted_IsSkipped_SoDropdownIsNotPermanentlyEmpty()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 99, ParentFid = 7 }] };

        (await Build(settings, new() { [99] = "1" })).Should().BeEmpty();
    }
    // ── Filter tree ──────────────────────────────────────────────────────────

    private static FilterCondition Literal(long fieldId, string op, string value) =>
        new() { FieldId = fieldId, Operator = op, Value = value, ValueMode = "literal" };

    private static FilterCondition FromForm(long fieldId, string op, long formFid) =>
        new() { FieldId = fieldId, Operator = op, ValueMode = "parentField", ValueFieldId = formFid };

    private static ReferenceSettings Tree(FilterGroup tree) =>
        new() { FilterTree = System.Text.Json.JsonSerializer.Serialize(tree) };

    private static FilterGroup And(params FilterCondition[] conditions) =>
        new() { Logic = "and", Nodes = conditions.Select(c => new FilterNode { Condition = c }).ToList() };

    [Fact]
    public async Task Tree_FormFieldComparison_BecomesLiteralWithSubmittedValue()
    {
        var settings = Tree(And(Literal(6, "eq", "Active"), FromForm(7, "gte", 12)));

        var clauses = await Build(settings, new() { [12] = " 5 " });

        clauses.Should().ContainSingle();
        var nodes = clauses[0].Tree!.Nodes;
        nodes[0].Condition!.Value.Should().Be("Active");
        nodes[1].Condition!.ValueMode.Should().Be("literal");
        nodes[1].Condition!.Value.Should().Be("5");
        nodes[1].Condition!.FieldId.Should().Be(7);
        clauses[0].TreeFields.Should().BeSameAs(StatusFields);
    }

    [Fact]
    public async Task Tree_SameRecordFieldComparison_IsKeptAsIs_AndNeedsNoFormValue()
    {
        // "Name is equal to the value in a Status field: Type" — both sides are on the parent record.
        var sameRecord = new FilterCondition { FieldId = 6, Operator = "eq", ValueMode = "field", ValueFieldId = 7 };

        var clauses = await Build(Tree(And(sameRecord)), new());

        var cond = clauses.Should().ContainSingle().Subject.Tree!.Nodes.Should().ContainSingle().Subject.Condition!;
        cond.ValueMode.Should().Be("field");
        cond.ValueFieldId.Should().Be(7);
        ReferenceFilterResolver.ControllingFids(Tree(And(sameRecord))).Should().BeEmpty();
    }

    [Fact]
    public async Task Tree_FormFormulaComparison_UsesTheValueTheFormWorkedOut()
    {
        // Name is equal to the value in a Task field: Task Code (a formula the form computes live).
        var clauses = await Build(Tree(And(FromForm(6, "eq", 13))), new() { [13] = "ENG-1" });

        var cond = clauses.Should().ContainSingle().Subject.Tree!.Nodes.Should().ContainSingle().Subject.Condition!;
        cond.FieldId.Should().Be(6);
        cond.ValueMode.Should().Be("literal");
        cond.Value.Should().Be("ENG-1");
    }

    [Fact]
    public async Task Tree_BlankFormValue_BecomesAConditionThatCannotHold()
    {
        var clauses = await Build(Tree(And(FromForm(6, "eq", 12))), new() { [12] = "  " });

        var cond = clauses.Should().ContainSingle().Subject.Tree!.Nodes[0].Condition!;
        cond.FieldId.Should().Be(3);   // Record ID# = -1 never matches
        cond.Value.Should().Be("-1");
    }

    [Fact]
    public async Task Tree_FormFieldNotSupplied_DropsThatConditionOnly()
    {
        var clauses = await Build(Tree(And(Literal(6, "eq", "Active"), FromForm(7, "eq", 12))), new());

        clauses.Should().ContainSingle().Subject.Tree!.Nodes.Should().ContainSingle()
            .Which.Condition!.Value.Should().Be("Active");
    }

    [Fact]
    public async Task Tree_OnlyUnsuppliedFormComparisons_YieldsNoClause()
    {
        (await Build(Tree(And(FromForm(7, "eq", 12))), new())).Should().BeEmpty();
    }

    [Fact]
    public async Task Tree_ComparisonToDeletedFormField_IsDropped()
    {
        (await Build(Tree(And(FromForm(7, "eq", 99))), new() { [99] = "1" })).Should().BeEmpty();
    }

    [Fact]
    public async Task Tree_NestedGroup_IsResolvedAndEmptyGroupsArePruned()
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new() { Condition = Literal(6, "eq", "Active") },
                new() { Group = new FilterGroup { Logic = "or", Nodes = [new() { Condition = FromForm(7, "eq", 12) }, new() { Condition = Literal(7, "eq", "9") }] } },
                new() { Group = new FilterGroup { Logic = "or", Nodes = [new() { Condition = FromForm(7, "eq", 77) }] } },   // 77 not on the form
            ],
        };

        var clauses = await Build(Tree(tree), new() { [12] = "3" });

        var nodes = clauses.Should().ContainSingle().Subject.Tree!.Nodes;
        nodes.Should().HaveCount(2);                       // empty group removed
        nodes[1].Group!.Logic.Should().Be("or");
        nodes[1].Group!.Nodes[0].Condition!.Value.Should().Be("3");
    }

    [Theory]
    [InlineData("not a number", true)]    // Type (fid 7) is Number
    [InlineData("12.5", false)]
    public async Task Tree_ValueTheColumnCannotHold_IsTreatedAsNoSelection(string value, bool becomesImpossible)
    {
        var clauses = await Build(Tree(And(FromForm(7, "eq", 12))), new() { [12] = value });

        var cond = clauses.Should().ContainSingle().Subject.Tree!.Nodes[0].Condition!;
        (cond.FieldId == 3 && cond.Value == "-1").Should().Be(becomesImpossible);
    }

    [Fact]
    public void EqualityPairs_TakesEqualsComparisonsToFormFields_InAndGroups_PlusDirectLegacyConditions()
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new() { Condition = FromForm(7, "eq", 12) },
                new() { Condition = FromForm(6, "contains", 13) },                       // not an equals: can't fill
                new() { Condition = Literal(6, "eq", "x") },                              // a literal, not a form field
                new() { Group = new FilterGroup { Logic = "and", Nodes = [new() { Condition = FromForm(6, "eq", 14) }] } },
                new() { Group = new FilterGroup { Logic = "or", Nodes = [new() { Condition = FromForm(7, "eq", 15) }, new() { Condition = FromForm(6, "eq", 16) }] } },
            ],
        };
        var settings = Tree(tree);
        settings.FilterConditions = [new() { FormFid = 30, ParentFid = 7 }, new() { FormFid = 31, JunctionTableId = 99, JunctionParentFid = 6, JunctionValueFid = 7 }];

        ReferenceFilterResolver.EqualityPairs(settings).Should().BeEquivalentTo(new[] { (7, 30), (7, 12), (6, 14) });
    }

    [Fact]
    public void EqualityPairs_NoneWhileTheFilterIsSwitchedOff()
    {
        var settings = Tree(And(FromForm(7, "eq", 12)));
        settings.FilterDisabled = true;

        ReferenceFilterResolver.EqualityPairs(settings).Should().BeEmpty();
        ReferenceFilterResolver.EqualityPairs(null).Should().BeEmpty();
    }

    [Fact]
    public void ControllingFids_CombinesLegacyConditionsAndTreeFormComparisons()
    {
        var settings = Tree(And(FromForm(7, "eq", 12), Literal(6, "eq", "x")));
        settings.FilterConditions = [new() { FormFid = 30, ParentFid = 7 }];

        ReferenceFilterResolver.ControllingFids(settings).Should().BeEquivalentTo(new[] { 12, 30 });
        ReferenceFilterResolver.ControllingFids(Tree(And(Literal(6, "eq", "x")))).Should().BeEmpty();
        ReferenceFilterResolver.ControllingFids(null).Should().BeEmpty();
    }

    [Fact]
    public void HasFilter_IsTrueForTreeOrLegacy_AndFalseForEmptyOrUnreadableTree()
    {
        ReferenceFilterResolver.HasFilter(Tree(And(Literal(6, "eq", "x")))).Should().BeTrue();
        ReferenceFilterResolver.HasFilter(new ReferenceSettings { FilterConditions = [new() { FormFid = 1, ParentFid = 2 }] }).Should().BeTrue();
        ReferenceFilterResolver.HasFilter(Tree(new FilterGroup())).Should().BeFalse();
        ReferenceFilterResolver.HasFilter(new ReferenceSettings { FilterTree = "{not json" }).Should().BeFalse();
        ReferenceFilterResolver.HasFilter(null).Should().BeFalse();
    }

    // ── Switched off / show all while blank ──────────────────────────────────

    [Fact]
    public async Task FilterDisabled_YieldsNoClauses_AndHasFilterIsFalse()
    {
        var settings = Tree(And(Literal(6, "eq", "Active"), FromForm(7, "eq", 12)));
        settings.FilterConditions = [new() { FormFid = 12, ParentFid = 7 }];
        settings.FilterDisabled = true;

        (await Build(settings, new() { [12] = "" })).Should().BeEmpty();
        ReferenceFilterResolver.HasFilter(settings).Should().BeFalse();
        ReferenceFilterResolver.ControllingFids(settings).Should().Equal(12);   // kept for the delete guard
    }

    [Fact]
    public async Task ShowAllWhenBlank_Tree_DropsTheBlankComparison_KeepsTheRest()
    {
        var settings = Tree(And(Literal(6, "eq", "Active"), FromForm(7, "eq", 12)));
        settings.ShowAllWhenBlank = true;

        var clauses = await Build(settings, new() { [12] = "  " });

        clauses.Should().ContainSingle().Subject.Tree!.Nodes.Should().ContainSingle()
            .Which.Condition!.Value.Should().Be("Active");
    }

    [Fact]
    public async Task ShowAllWhenBlank_OnlyAComparison_YieldsNoClause()
    {
        var settings = Tree(And(FromForm(7, "eq", 12)));
        settings.ShowAllWhenBlank = true;

        (await Build(settings, new() { [12] = null })).Should().BeEmpty();
    }

    [Fact]
    public async Task ShowAllWhenBlank_StillFiltersOnceAValueIsChosen()
    {
        var settings = Tree(And(FromForm(7, "eq", 12)));
        settings.ShowAllWhenBlank = true;

        (await Build(settings, new() { [12] = "4" })).Should().ContainSingle()
            .Subject.Tree!.Nodes[0].Condition!.Value.Should().Be("4");
    }

    [Fact]
    public async Task ShowAllWhenBlank_StillMatchesNothingForAValueTheColumnCannotHold()
    {
        var settings = Tree(And(FromForm(7, "eq", 12)));   // Type (fid 7) is Number
        settings.ShowAllWhenBlank = true;

        var cond = (await Build(settings, new() { [12] = "abc" })).Should().ContainSingle().Subject.Tree!.Nodes[0].Condition!;
        (cond.FieldId, cond.Value).Should().Be((3L, "-1"));
    }

    [Fact]
    public async Task ShowAllWhenBlank_LegacyCondition_IsLeftOutWhileBlank()
    {
        var settings = new ReferenceSettings { FilterConditions = [new() { FormFid = 12, ParentFid = 7 }], ShowAllWhenBlank = true };

        (await Build(settings, new() { [12] = "" })).Should().BeEmpty();
        (await Build(settings, new() { [12] = "5" })).Should().ContainSingle().Which.Value.Should().Be("5");
    }

    [Fact]
    public async Task StaleCondition_PointingAtDeletedField_IsSkipped()
    {
        var settings = new ReferenceSettings
        {
            FilterConditions =
            [
                new() { FormFid = 12, ParentFid = 999 },
                new() { FormFid = 12, JunctionTableId = 99, JunctionParentFid = 6, JunctionValueFid = 999 },
            ],
        };

        (await Build(settings, new() { [12] = "1" })).Should().BeEmpty();
    }
}
