using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships.Queries;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

/// <summary>With "show all until chosen", an Activity picked before the Task Type fills the Task Type: the values a picked
/// Activity fills into the Task's form.</summary>
public class GetControllingFillQueryHandlerTests
{
    private readonly IRelationshipRepository _rels = Substitute.For<IRelationshipRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();

    private static readonly Guid RelId = Guid.NewGuid();
    private static readonly Relationship Rel = new() { Id = 1, PublicId = RelId, ParentTableId = 5, ChildTableId = 20, ReferenceFieldId = 80 };
    private static readonly AppTable Activities = new() { Id = 5, Name = "Activities" };
    private static readonly AppField ParentTaskType = new() { Id = 61, Fid = 7, Name = "Task_Type", TypeCode = "SingleSelect" };
    private static readonly List<AppField> ActivityFields = [new() { Id = 60, Fid = 6, Name = "Activity_Name", TypeCode = "Text" }, ParentTaskType];

    public GetControllingFillQueryHandlerTests()
    {
        _rels.GetByPublicIdAsync(RelId, Arg.Any<CancellationToken>()).Returns(Rel);
        _tables.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(Activities);
        _fields.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(ActivityFields);
        _records.ExistsAsync(Activities, 6, Arg.Any<CancellationToken>()).Returns(true);   // "Wireframe"
        _records.GetRowsByIdsAsync(Activities, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [6] = new Dictionary<string, object?> { ["Id"] = 6L, ["f_7"] = "Design" } });
        _enforcer.GetTableAccessAsync(Activities, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = true });
    }

    private void TasksWith(bool showAll = true, string op = "eq", bool filterDisabled = false)
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = op, ValueMode = "parentField", ValueFieldId = 7 } }],
        };
        var settings = new ReferenceSettings
        {
            ParentTableId = 5, FilterTree = JsonSerializer.Serialize(tree),
            ShowAllWhenBlank = showAll ? true : null, FilterDisabled = filterDisabled ? true : null,
        };
        _fields.ListByTableAsync(20, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 70, Fid = 7, Name = "Task_Type", TypeCode = "SingleSelect" },
            new() { Id = 80, Fid = 8, Name = "Activity", TypeCode = "Reference", Settings = JsonSerializer.Serialize(settings) },
        });
    }

    private Task<IReadOnlyDictionary<int, string>> Fill(string parentRecord = "6") =>
        new GetControllingFillQueryHandler(_rels, _tables, _fields, _records, _enforcer).HandleAsync(RelId, parentRecord);

    [Fact]
    public async Task PickedActivity_FillsTheTaskTypeWithItsTaskType()
    {
        TasksWith();

        (await Fill()).Should().BeEquivalentTo(new Dictionary<int, string> { [7] = "Design" });
    }

    [Fact]
    public async Task ShowAllOff_FillsNothing()
    {
        TasksWith(showAll: false);

        (await Fill()).Should().BeEmpty();
    }

    [Fact]
    public async Task FilterSwitchedOff_FillsNothing()
    {
        TasksWith(filterDisabled: true);

        (await Fill()).Should().BeEmpty();
    }

    [Fact]
    public async Task ANonEqualsComparison_CannotFill()
    {
        TasksWith(op: "contains");

        (await Fill()).Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownRecord_FillsNothing()
    {
        TasksWith();

        (await Fill("999")).Should().BeEmpty();
    }

    [Fact]
    public async Task ParentFieldTheRoleCannotSee_IsNotFilled()
    {
        TasksWith();
        _enforcer.GetTableAccessAsync(Activities, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(new TableAccessContext { Unrestricted = false, VisibleFields = [ActivityFields[0]] });

        (await Fill()).Should().BeEmpty();
    }

    [Fact]
    public async Task BlankParentValue_FillsNothing()
    {
        TasksWith();
        _records.GetRowsByIdsAsync(Activities, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>> { [6] = new Dictionary<string, object?> { ["Id"] = 6L, ["f_7"] = null } });

        (await Fill()).Should().BeEmpty();
    }
}
