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

/// <summary>The save check of a dependent dropdown when an update changes only the controlling field (Task Type) and
/// leaves the Reference (Activity) out — what Grid Edit and the API send. The stored Activity must still be allowed.</summary>
public class ReferenceWriteValidatorStoredParentTests
{
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();

    private static readonly AppTable Activities = new() { Id = 5, Name = "Activities" };

    private static readonly List<AppField> ActivityFields =
    [
        new() { Id = 60, Fid = 6, Name = "Activity_Name", TypeCode = "Text" },
        new() { Id = 61, Fid = 7, Name = "Task_Type", TypeCode = "SingleSelect" },
    ];

    private const long WriteCode = 1;   // an Activity of Task Type "Development"

    public ReferenceWriteValidatorStoredParentTests()
    {
        _tables.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(Activities);
        _fields.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(ActivityFields);
        _records.ExistsAsync(Activities, WriteCode, Arg.Any<CancellationToken>()).Returns(true);
    }

    /// <summary>Tasks: Task Type (fid 7) and Activity (fid 8), filtered on Activities.Task Type = the form's Task Type.</summary>
    private static List<AppField> TaskFields(bool showAllWhenBlank = false, bool filterDisabled = false)
    {
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "eq", ValueMode = "parentField", ValueFieldId = 7 } }],
        };
        var settings = new ReferenceSettings
        {
            ParentTableId = 5,
            FilterTree = JsonSerializer.Serialize(tree),
            ShowAllWhenBlank = showAllWhenBlank ? true : null,
            FilterDisabled = filterDisabled ? true : null,
        };
        return
        [
            new() { Id = 70, Fid = 7, Name = "Task_Type", Label = "Task Type", TypeCode = "SingleSelect" },
            new() { Id = 80, Fid = 8, Name = "Activity", Label = "Activity", TypeCode = "Reference", Settings = JsonSerializer.Serialize(settings) },
        ];
    }

    private static readonly Dictionary<long, object?> StoredDevelopmentWriteCode = new() { [7] = "Development", [8] = WriteCode };

    private Task<Dictionary<long, object?>> Validate(List<AppField> fields, Dictionary<long, object?> values,
        Dictionary<long, object?>? existing) =>
        ReferenceWriteValidator.ValidateAsync(fields, values, _tables, _fields, _records, relRepo: null, default, existing);

    private void StoredActivityMatches(bool matches) =>
        _records.MatchesReferenceFilterAsync(Activities, WriteCode, Arg.Any<IReadOnlyList<ReferenceFilterClause>>(), Arg.Any<CancellationToken>())
            .Returns(matches);

    [Fact]
    public async Task OnlyTaskTypeChanged_StoredActivityNoLongerMatches_IsRejected()
    {
        StoredActivityMatches(false);

        var act = () => Validate(TaskFields(), new() { [7] = "Testing" }, StoredDevelopmentWriteCode);

        (await act.Should().ThrowAsync<ValidationException>())
            .Which.Errors["Activity"].Should().ContainSingle().Which.Should().Contain("Activity no longer matches the new selection");
    }

    [Fact]
    public async Task OnlyTaskTypeChanged_StoredActivityStillMatches_IsSaved()
    {
        StoredActivityMatches(true);

        await Validate(TaskFields(), new() { [7] = "Development " }, new() { [7] = "Design", [8] = WriteCode });

        await _records.Received(1).MatchesReferenceFilterAsync(Activities, WriteCode, Arg.Any<IReadOnlyList<ReferenceFilterClause>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TaskTypeResentUnchanged_DoesNotJudgeTheStoredActivity()
    {
        StoredActivityMatches(false);

        await Validate(TaskFields(), new() { [7] = "Development" }, StoredDevelopmentWriteCode);

        await _records.DidNotReceiveWithAnyArgs().MatchesReferenceFilterAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task ActivityClearedInTheSameUpdate_IsNotJudged()
    {
        StoredActivityMatches(false);

        await Validate(TaskFields(), new() { [7] = "Testing", [8] = null }, StoredDevelopmentWriteCode);

        await _records.DidNotReceiveWithAnyArgs().MatchesReferenceFilterAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task ShowAllWhenBlank_TaskTypeCleared_KeepsTheActivity()
    {
        StoredActivityMatches(false);

        await Validate(TaskFields(showAllWhenBlank: true), new() { [7] = "" }, StoredDevelopmentWriteCode);

        await _records.DidNotReceiveWithAnyArgs().MatchesReferenceFilterAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task FilterSwitchedOff_DoesNotJudgeTheStoredActivity()
    {
        StoredActivityMatches(false);

        await Validate(TaskFields(filterDisabled: true), new() { [7] = "Testing" }, StoredDevelopmentWriteCode);

        await _records.DidNotReceiveWithAnyArgs().MatchesReferenceFilterAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Create_WithoutStoredValues_HasNoStoredActivityToJudge()
    {
        StoredActivityMatches(false);

        await Validate(TaskFields(), new() { [7] = "Testing" }, existing: null);

        await _records.DidNotReceiveWithAnyArgs().MatchesReferenceFilterAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task StoredActivityThatNoLongerExists_IsLeftAlone()
    {
        StoredActivityMatches(false);
        _records.ExistsAsync(Activities, WriteCode, Arg.Any<CancellationToken>()).Returns(false);

        await Validate(TaskFields(), new() { [7] = "Testing" }, StoredDevelopmentWriteCode);

        await _records.DidNotReceiveWithAnyArgs().MatchesReferenceFilterAsync(default!, default, default!, default);
    }
}
