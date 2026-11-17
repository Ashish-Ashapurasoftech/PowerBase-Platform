using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.UnitTests.Relationships;

/// <summary>The save check of a dependent dropdown whose controlling field is a Lookup: SubTasks.Task Reference offers
/// only Tasks whose Task Type equals the SubTask's "Related Project Type" — a Lookup of Project → Project Type. The
/// Lookup has no column, so its value is worked out from the Project the write leaves on the record.</summary>
public class ReferenceWriteValidatorLookupTests
{
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();

    private static readonly AppTable Tasks = new() { Id = 5, Name = "Tasks" };
    private static readonly AppTable Projects = new() { Id = 6, Name = "Projects" };

    private static readonly List<AppField> TaskFields = [new() { Id = 57, Fid = 7, Name = "Task_Type", TypeCode = "Text" }];
    private static readonly AppField ProjectType = new() { Id = 67, Fid = 7, Name = "Project_Type", TypeCode = "Text" };

    private const long SmokeTesting = 5;    // a Task of Task Type "Testing"
    private const long Launch = 1;          // a Project of Project Type "Start"
    private const long QA = 3;              // a Project of Project Type "Testing"

    /// <summary>SubTasks: SubTask Name (6), Task Reference (9, filtered), Project (10), Related Project Type (11, Lookup).</summary>
    private static readonly List<AppField> SubTaskFields = BuildSubTaskFields();

    private static List<AppField> BuildSubTaskFields()
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "eq", ValueMode = "parentField", ValueFieldId = 11 } }],
        };
        return
        [
            new() { Id = 106, Fid = 6, Name = "SubTask_Name", TypeCode = "Text" },
            new()
            {
                Id = 109, Fid = 9, Name = "Task_Reference", Label = "Task Reference", TypeCode = "Reference",
                Settings = JsonSerializer.Serialize(new ReferenceSettings { ParentTableId = 5, FilterTree = JsonSerializer.Serialize(tree) }),
            },
            new()
            {
                Id = 110, Fid = 10, Name = "Project", TypeCode = "Reference",
                Settings = JsonSerializer.Serialize(new ReferenceSettings { ParentTableId = 6 }),
            },
            new()
            {
                Id = 111, Fid = 11, Name = "Related_Project_Type", TypeCode = "Lookup",
                Settings = JsonSerializer.Serialize(new LookupSettings { ReferenceFid = 10, SourceTableId = 6, SourceFid = 7, SourceTypeCode = "Text" }),
            },
        ];
    }

    private readonly List<IReadOnlyList<ReferenceFilterClause>> _judged = [];

    public ReferenceWriteValidatorLookupTests()
    {
        _tables.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(Tasks);
        _tables.GetByIdAsync(6, Arg.Any<CancellationToken>()).Returns(Projects);
        _fields.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(TaskFields);
        _fields.ListByTableAsync(6, Arg.Any<CancellationToken>()).Returns(new List<AppField> { ProjectType });
        _records.ExistsAsync(Tasks, SmokeTesting, Arg.Any<CancellationToken>()).Returns(true);
        _records.ExistsAsync(Projects, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        ProjectTypeIs(Launch, "Start");
        ProjectTypeIs(QA, "Testing");
    }

    private void ProjectTypeIs(long project, string type) =>
        _records.GetRowsByIdsAsync(Projects, Arg.Any<IReadOnlyList<AppField>>(),
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(project)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, object?>>
            {
                [project] = new Dictionary<string, object?> { ["Id"] = project, [EncryptedRowFilter.Column(ProjectType)] = type },
            });

    private void SmokeTestingMatches(bool matches) =>
        _records.MatchesReferenceFilterAsync(Tasks, SmokeTesting, Arg.Do<IReadOnlyList<ReferenceFilterClause>>(c => _judged.Add(c)),
                Arg.Any<CancellationToken>())
            .Returns(matches);

    /// <summary>The value the Task Type condition was judged against.</summary>
    private string? JudgedValue() =>
        _judged.Should().ContainSingle().Which.Should().ContainSingle().Which.Tree!.Nodes.Should().ContainSingle().Which.Condition!.Value;

    private Task<Dictionary<long, object?>> Validate(Dictionary<long, object?> values, Dictionary<long, object?>? existing) =>
        ReferenceWriteValidator.ValidateAsync(SubTaskFields, values, _tables, _fields, _records, relRepo: null, default, existing);

    private static Dictionary<long, object?> StoredQaSmokeTesting() => new() { [6] = "Write cases", [9] = SmokeTesting, [10] = QA };

    [Fact]
    public async Task GridEdit_OnlyProjectChanged_StoredTaskNoLongerMatchesTheLookup_IsRejected()
    {
        SmokeTestingMatches(false);

        var act = () => Validate(new() { [10] = Launch }, StoredQaSmokeTesting());

        (await act.Should().ThrowAsync<ValidationException>())
            .Which.Errors["Task_Reference"].Should().ContainSingle().Which.Should().Contain("no longer matches the new selection");
        JudgedValue().Should().Be("Start");
    }

    [Fact]
    public async Task OnlyProjectChanged_StoredTaskStillMatches_IsSaved()
    {
        SmokeTestingMatches(true);

        await Validate(new() { [10] = QA }, new() { [9] = SmokeTesting, [10] = Launch });

        JudgedValue().Should().Be("Testing");
    }

    [Fact]
    public async Task Form_SendsTaskProjectAndAStaleLookupValue_JudgedOnTheNewProject()
    {
        SmokeTestingMatches(false);

        var act = () => Validate(new() { [9] = SmokeTesting, [10] = Launch, [11] = "Testing" }, StoredQaSmokeTesting());

        await act.Should().ThrowAsync<ValidationException>();
        JudgedValue().Should().Be("Start");
    }

    [Fact]
    public async Task Create_WithProjectAndTask_JudgedOnTheProjectsType()
    {
        SmokeTestingMatches(false);

        var act = () => Validate(new() { [6] = "New", [9] = SmokeTesting, [10] = Launch }, existing: null);

        (await act.Should().ThrowAsync<ValidationException>())
            .Which.Errors["Task_Reference"].Should().ContainSingle().Which.Should().Contain("not allowed for the current selection");
        JudgedValue().Should().Be("Start");
    }

    [Fact]
    public async Task OnlyTaskChanged_JudgedOnTheStoredProject()
    {
        SmokeTestingMatches(true);

        await Validate(new() { [9] = SmokeTesting }, new() { [9] = 2L, [10] = QA });

        JudgedValue().Should().Be("Testing");
    }

    [Fact]
    public async Task UnrelatedEdit_DoesNotJudgeTheStoredTask()
    {
        SmokeTestingMatches(false);

        await Validate(new() { [6] = "Renamed" }, StoredQaSmokeTesting());

        await _records.DidNotReceiveWithAnyArgs().MatchesReferenceFilterAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task ProjectResentUnchanged_DoesNotJudgeTheStoredTask()
    {
        SmokeTestingMatches(false);

        await Validate(new() { [10] = QA }, StoredQaSmokeTesting());

        await _records.DidNotReceiveWithAnyArgs().MatchesReferenceFilterAsync(default!, default, default!, default);
    }
}
