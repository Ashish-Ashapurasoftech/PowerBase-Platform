using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>The list's own switch for an import's schedule: only the on/off flag changes, and switching on works out the next run and runs the import as the person
/// who switched it on.</summary>
public class ImportScheduleToggleTests
{
    private static readonly Guid DefinitionId = Guid.NewGuid();
    private static readonly Guid DestinationId = Guid.NewGuid();

    private readonly IImportDefinitionRepository _definitions = Substitute.For<IImportDefinitionRepository>();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly ImportDefinition _def;
    private readonly SetImportScheduleEnabledHandler _sut;

    public ImportScheduleToggleTests(bool enabled = true)
    {
        _def = new ImportDefinition { Id = 4, PublicId = DefinitionId, DestinationTableId = 11, RunAsUserId = 2 };
        SetSchedule(enabled);
        _definitions.GetByPublicIdAsync(DefinitionId, Arg.Any<CancellationToken>()).Returns(_def);
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, PublicId = DestinationId });
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(9L);
        _sut = new SetImportScheduleEnabledHandler(tables, _access, _definitions, user);
    }

    private void SetSchedule(bool enabled)
    {
        var schedule = new ImportSchedule { Enabled = enabled, Type = ImportScheduleType.Hourly, Interval = 1, TimeZone = "UTC", AnchorOn = DateTime.UtcNow.AddDays(-3) };
        _def.ScheduleJson = ImportJson.Serialize(schedule);
        _def.NextRunOn = enabled ? DateTime.UtcNow.AddMinutes(20) : null;
    }

    private ImportSchedule Saved() => ImportJson.Deserialize<ImportSchedule>(_def.ScheduleJson)!;

    [Fact]
    public async Task Switching_off_pauses_the_schedule_and_clears_the_next_run_and_nothing_else_changes()
    {
        var anchor = Saved().AnchorOn;

        var state = await _sut.HandleAsync(DefinitionId, false, default);

        state.Should().Match<ImportScheduleState>(s => !s.Enabled && s.NextRunOn == null && s.Summary!.EndsWith("(paused)"));
        Saved().Should().Match<ImportSchedule>(s => !s.Enabled && s.Type == ImportScheduleType.Hourly && s.Interval == 1 && s.AnchorOn == anchor);
        _def.NextRunOn.Should().BeNull();
        _def.RunAsUserId.Should().Be(2, "pausing does not hand the import to anyone");
        await _definitions.Received(1).UpdateAsync(_def, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Switching_on_works_out_the_next_run_from_now_and_runs_it_as_the_person_who_switched_it_on()
    {
        SetSchedule(false);

        var state = await _sut.HandleAsync(DefinitionId, true, default);

        state.Enabled.Should().BeTrue();
        state.NextRunOn.Should().BeAfter(DateTime.UtcNow.AddSeconds(-5));
        state.Summary.Should().NotContain("paused");
        _def.NextRunOn.Should().Be(state.NextRunOn);
        _def.RunAsUserId.Should().Be(9);
        _def.ModifiedBy.Should().Be(9);
        Saved().Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Asking_for_what_it_already_is_changes_nothing()
    {
        var state = await _sut.HandleAsync(DefinitionId, true, default);

        state.Enabled.Should().BeTrue();
        await _definitions.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task An_import_with_no_schedule_has_nothing_to_switch()
    {
        _def.ScheduleJson = null;

        var act = () => _sut.HandleAsync(DefinitionId, true, default);

        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*no schedule*");
        await _definitions.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task An_import_that_needs_attention_is_not_switched_on_but_can_be_switched_off()
    {
        _def.NeedsAttention = true;
        _def.AttentionReason = "A field it writes to was deleted.";
        SetSchedule(false);

        var on = () => _sut.HandleAsync(DefinitionId, true, default);
        (await on.Should().ThrowAsync<ValidationException>()).WithMessage("*needs attention*");

        SetSchedule(true);
        (await _sut.HandleAsync(DefinitionId, false, default)).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Without_the_right_to_change_the_import_nothing_is_changed()
    {
        _access.RequirePermissionByTablePublicIdAsync(DestinationId, PermissionCodes.RecordsCreate, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new UnauthorizedActionException("create records")));

        var act = () => _sut.HandleAsync(DefinitionId, false, default);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await _definitions.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task An_import_that_does_not_exist_is_not_found()
    {
        var act = () => _sut.HandleAsync(Guid.NewGuid(), true, default);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
