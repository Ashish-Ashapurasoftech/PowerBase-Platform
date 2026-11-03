using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

/// <summary>A Reference in a dropdown filter tree is compared as the key its parent shows, as in a summary's matching
/// criteria (<see cref="ReferenceKeys"/>).</summary>
public class ReferenceFilterKeyTests
{
    private readonly IRelationshipRepository _relRepo = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();

    // Employees (50) is the form; its "Related Department" (14) dropdown lists Departments (60). Departments and Employees
    // both reference Divisions (70), whose key is Division Name (fid 6). Departments set no key.
    private static readonly List<AppField> EmployeeFields =
    [
        new() { Id = 512, Fid = 12, AppTableId = 50, Name = "Division Name", TypeCode = "Text" },
        new() { Id = 513, Fid = 13, AppTableId = 50, Name = "Division", TypeCode = "Reference",
            Settings = """{"relationshipId":901,"parentTableId":70}""" },
        new() { Id = 514, Fid = 14, AppTableId = 50, Name = "Related Department", TypeCode = "Reference",
            Settings = """{"relationshipId":902,"parentTableId":60}""" },
    ];

    private static readonly List<AppField> DepartmentFields =
    [
        new() { Id = 603, Fid = 3, AppTableId = 60, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
        new() { Id = 606, Fid = 6, AppTableId = 60, Name = "Name", TypeCode = "Text" },
        new() { Id = 608, Fid = 8, AppTableId = 60, Name = "Related Division", TypeCode = "Reference",
            Settings = """{"relationshipId":801,"parentTableId":70}""" },
    ];

    private static readonly AppField DivisionName = new() { Id = 706, Fid = 6, AppTableId = 70, Name = "Division Name", TypeCode = "Text" };
    private static readonly AppTable Divisions = new() { Id = 70, KeyFieldId = 706 };

    private static readonly long DivisionKey = ReferenceKeys.StandInFid(8);

    public ReferenceFilterKeyTests()
    {
        _relRepo.ListByChildTableAsync(60, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 801, ParentTableId = 70, ChildTableId = 60, ReferenceFid = 8 },
        });
        _relRepo.ListByChildTableAsync(50, Arg.Any<CancellationToken>()).Returns(new List<Relationship>
        {
            new() { Id = 901, ParentTableId = 70, ChildTableId = 50, ReferenceFid = 13 },
            new() { Id = 902, ParentTableId = 60, ChildTableId = 50, ReferenceFid = 14 },
        });
        _tableRepo.GetByIdAsync(70, Arg.Any<CancellationToken>()).Returns(Divisions);
        _tableRepo.GetByIdAsync(60, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 60 });
        _fieldRepo.ListByTableAsync(70, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 703, Fid = 3, AppTableId = 70, Name = "Record ID#", TypeCode = "Number", IsSystem = true },
            DivisionName,
        });
        _fieldRepo.ListByTableAsync(60, Arg.Any<CancellationToken>()).Returns(DepartmentFields);
        // Division 5 is "Engineering".
        _recordRepo.GetRowsByIdsAsync(Divisions, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [5] = new Dictionary<string, object?> { ["Id"] = 5L, ["f_6"] = "Engineering" },
            });
    }

    private static ReferenceSettings Settings(params FilterCondition[] conditions) => new()
    {
        ParentTableId = 60,
        FilterTree = System.Text.Json.JsonSerializer.Serialize(new FilterGroup
        {
            Logic = "and", Nodes = conditions.Select(c => new FilterNode { Condition = c }).ToList(),
        }),
    };

    private async Task<ReferenceFilterClause> Build(ReferenceSettings settings, Dictionary<int, string?> formValues, bool withKeys = true)
    {
        var clauses = await ReferenceFilterResolver.BuildAsync(settings, EmployeeFields, DepartmentFields, formValues,
            _tableRepo, _fieldRepo, default, relRepo: withKeys ? _relRepo : null, recordRepo: _recordRepo);
        return clauses.Should().ContainSingle().Subject;
    }

    private static FilterCondition Only(ReferenceFilterClause clause) =>
        clause.Tree!.Nodes.Should().ContainSingle().Subject.Condition!;

    [Fact]
    public async Task A_parent_Reference_is_filtered_on_the_key_its_parent_shows()
    {
        var clause = await Build(Settings(new FilterCondition { FieldId = 8, Operator = "eq", Value = "Engineering", ValueMode = "literal" }), new());

        var cond = Only(clause);
        cond.FieldId.Should().Be(DivisionKey);
        cond.Value.Should().Be("Engineering");
        var standIn = clause.TreeFields!.Should().ContainSingle(f => f.Fid == DivisionKey).Subject;
        standIn.TypeCode.Should().Be("Lookup");
        var lookup = SummaryLookupSources.Settings(standIn)!;
        lookup.ReferenceFid.Should().Be(8);
        lookup.SourceTableId.Should().Be(70);
        lookup.SourceFid.Should().Be(6);
    }

    [Fact]
    public async Task A_same_record_comparison_to_a_parent_Reference_compares_to_its_key()
    {
        var clause = await Build(Settings(new FilterCondition { FieldId = 6, Operator = "eq", ValueMode = "field", ValueFieldId = 8 }), new());

        var cond = Only(clause);
        cond.FieldId.Should().Be(6);
        cond.ValueFieldId.Should().Be(DivisionKey);
    }

    [Fact]
    public async Task A_form_Reference_is_compared_as_the_key_of_the_record_picked_in_it()
    {
        // Department Name is equal to the value in an Employee field: Division (Record ID# 5 picked).
        var clause = await Build(Settings(new FilterCondition { FieldId = 6, Operator = "eq", ValueMode = "parentField", ValueFieldId = 13 }),
            new() { [13] = "5" });

        var cond = Only(clause);
        cond.FieldId.Should().Be(6);
        cond.Value.Should().Be("Engineering");
    }

    [Fact]
    public async Task A_form_Reference_compared_to_a_parent_Reference_of_the_same_table_stays_a_Record_ID()
    {
        // The classic dependent dropdown: Departments whose Division is the form's Division.
        var clause = await Build(Settings(new FilterCondition { FieldId = 8, Operator = "eq", ValueMode = "parentField", ValueFieldId = 13 }),
            new() { [13] = "5" });

        var cond = Only(clause);
        cond.FieldId.Should().Be(8);
        cond.Value.Should().Be("5");
    }

    [Fact]
    public async Task A_parent_Reference_compared_to_a_form_text_field_is_compared_as_its_key()
    {
        var clause = await Build(Settings(new FilterCondition { FieldId = 8, Operator = "eq", ValueMode = "parentField", ValueFieldId = 12 }),
            new() { [12] = "Engineering" });

        var cond = Only(clause);
        cond.FieldId.Should().Be(DivisionKey);
        cond.Value.Should().Be("Engineering");
    }

    [Fact]
    public async Task A_form_Reference_pointing_at_a_record_that_is_gone_matches_nothing()
    {
        var clause = await Build(Settings(new FilterCondition { FieldId = 6, Operator = "eq", ValueMode = "parentField", ValueFieldId = 13 }),
            new() { [13] = "99" });

        var cond = Only(clause);
        cond.FieldId.Should().Be(3);
        cond.Value.Should().Be("-1");
    }

    [Fact]
    public async Task Without_the_relationships_a_Reference_stays_its_Record_ID()
    {
        var clause = await Build(Settings(new FilterCondition { FieldId = 8, Operator = "eq", Value = "5", ValueMode = "literal" }), new(),
            withKeys: false);

        Only(clause).FieldId.Should().Be(8);
        clause.TreeFields.Should().BeSameAs(DepartmentFields);
    }
}
