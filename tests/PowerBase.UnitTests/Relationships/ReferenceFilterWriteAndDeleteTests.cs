using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Relationships;

/// <summary>Dependent-dropdown behaviour on writes (ReferenceWriteValidator) and on deleting a
/// controlling field (ReferenceFilterDependencyGuard).</summary>
public class ReferenceFilterWriteAndDeleteTests
{
    private const string StatusSettings =
        """{"parentTableId":5,"filterConditions":[{"formFid":12,"parentFid":7}]}""";

    private static readonly AppTable StatusTable = new() { Id = 5, Name = "Statuses" };
    private static readonly AppTable TaskTable = new() { Id = 1, Name = "Tasks" };

    private static readonly List<AppField> TaskFields =
    [
        new() { Id = 30, Fid = 12, Name = "TaskType", TypeCode = "Number" },
        new() { Id = 31, Fid = 13, Name = "Status", TypeCode = "Reference", Settings = StatusSettings },
    ];

    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();

    public ReferenceFilterWriteAndDeleteTests()
    {
        _tables.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(StatusTable);
        _fields.ListByTableAsync(5, Arg.Any<CancellationToken>())
            .Returns(new List<AppField> { new() { Id = 2, Fid = 7, Name = "Type", TypeCode = "Number" } });
        _records.ExistsAsync(StatusTable, 3, Arg.Any<CancellationToken>()).Returns(true);
        _records.MatchesReferenceFilterAsync(StatusTable, 3, Arg.Any<IReadOnlyList<ReferenceFilterClause>>(), Arg.Any<CancellationToken>())
            .Returns(false);
    }

    private Task Validate(Dictionary<long, object?> values, Dictionary<long, object?>? existing = null) =>
        ReferenceWriteValidator.ValidateAsync(TaskFields, values, _tables, _fields, _records, null, default, existing);

    [Fact]
    public async Task Create_ValueNotAllowedForControllingValue_IsRejected()
    {
        var act = () => Validate(new() { [12] = "1", [13] = "3" });

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Update_NothingRelevantChanged_LegacyValueIsNotRevalidated()
    {
        await Validate(new() { [12] = "1", [13] = "3" }, new() { [12] = 1.0000m, [13] = 3L });

        await _records.DidNotReceive().MatchesReferenceFilterAsync(
            Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<IReadOnlyList<ReferenceFilterClause>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_ControllingValueChanged_IsRevalidatedAndRejected()
    {
        var act = () => Validate(new() { [12] = "2", [13] = "3" }, new() { [12] = 1m, [13] = 3L });

        await act.Should().ThrowAsync<ValidationException>();
    }


    [Fact]
    public async Task Update_OnlyReferenceChanged_IsJudgedAgainstStoredControllingValue()
    {
        var act = () => Validate(new() { [13] = "3" }, new() { [12] = 1m, [13] = 4L });

        await act.Should().ThrowAsync<ValidationException>();
    }
    [Fact]
    public async Task ControllingFieldNotInSubmission_IsNotJudged()
    {
        await Validate(new() { [13] = "3" });

        await _records.DidNotReceive().MatchesReferenceFilterAsync(
            Arg.Any<AppTable>(), Arg.Any<long>(), Arg.Any<IReadOnlyList<ReferenceFilterClause>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Guard_DeletingControllingField_IsBlocked()
    {
        var act = () => ReferenceFilterDependencyGuard.EnsureNotControlling(TaskTable, TaskFields, [TaskFields[0]]);

        act.Should().Throw<ConflictException>().WithMessage("*controls the dropdown*Status*");
    }

    [Fact]
    public void Guard_DeletingUnrelatedField_IsAllowed()
    {
        var other = new AppField { Id = 40, Fid = 20, Name = "Notes", TypeCode = "Text" };

        ReferenceFilterDependencyGuard.EnsureNotControlling(TaskTable, [.. TaskFields, other], [other]);
    }
}
