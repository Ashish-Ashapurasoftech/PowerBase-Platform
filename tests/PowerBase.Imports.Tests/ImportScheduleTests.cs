using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

public class ImportScheduleValidationTests
{
    private static ImportSchedule Daily() => new() { Type = "daily", Interval = 1, TimeOfDay = "09:30", TimeZone = "UTC" };

    [Fact]
    public void A_valid_daily_schedule_is_accepted()
        => ImportScheduling.Validate(Daily()).TimeOfDay.Should().Be("09:30");

    [Theory]
    [InlineData("")]
    [InlineData("hourly-ish")]
    public void An_unknown_type_is_rejected(string type)
    {
        var s = Daily(); s.Type = type;
        ((Action)(() => ImportScheduling.Validate(s))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void An_unknown_time_zone_is_rejected()
    {
        var s = Daily(); s.TimeZone = "Mars/Olympus";
        ((Action)(() => ImportScheduling.Validate(s))).Should().Throw<ValidationException>().WithMessage("*time zone*");
    }

    [Theory]
    [InlineData("9:30")]
    [InlineData("24:00")]
    [InlineData("09:30:15")]
    [InlineData("")]
    public void A_bad_time_of_day_is_rejected(string time)
    {
        var s = Daily(); s.TimeOfDay = time;
        ((Action)(() => ImportScheduling.Validate(s))).Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public void An_interval_out_of_range_is_rejected(int interval)
    {
        var s = Daily(); s.Interval = interval;
        ((Action)(() => ImportScheduling.Validate(s))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Weekly_needs_at_least_one_valid_day_and_sorts_and_dedupes_them()
    {
        var s = new ImportSchedule { Type = "weekly", TimeOfDay = "08:00", TimeZone = "UTC", Weekdays = [] };
        ((Action)(() => ImportScheduling.Validate(s))).Should().Throw<ValidationException>();
        s.Weekdays = [7];
        ((Action)(() => ImportScheduling.Validate(s))).Should().Throw<ValidationException>();
        s.Weekdays = [5, 1, 5];
        ImportScheduling.Validate(s).Weekdays.Should().Equal(1, 5);
    }

    [Theory]
    [InlineData("1,15,LAST", "1,15,last")]
    [InlineData(" 31 , 31 ", "31")]
    public void Month_days_are_normalised(string input, string expected)
    {
        var s = new ImportSchedule { Type = "monthly", TimeOfDay = "08:00", TimeZone = "UTC", MonthDays = input };
        ImportScheduling.Validate(s).MonthDays.Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("32")]
    [InlineData("first")]
    [InlineData("")]
    [InlineData(",")]
    public void Bad_month_days_are_rejected(string input)
    {
        var s = new ImportSchedule { Type = "monthly", TimeOfDay = "08:00", TimeZone = "UTC", MonthDays = input };
        ((Action)(() => ImportScheduling.Validate(s))).Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData("0 9 * * 1-5")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 */2 * * *")]
    public void Sensible_cron_expressions_are_accepted(string cron)
        => ImportScheduling.Validate(new ImportSchedule { Type = "custom", Cron = cron, TimeZone = "UTC" }).Cron.Should().Be(cron);

    [Theory]
    [InlineData("* * * * *")]        // every minute
    [InlineData("*/5 * * * *")]      // every five minutes
    [InlineData("0,5 9 * * *")]      // two runs five minutes apart, otherwise rare
    public void A_cron_that_fires_more_often_than_the_limit_is_rejected(string cron)
        => ((Action)(() => ImportScheduling.Validate(new ImportSchedule { Type = "custom", Cron = cron, TimeZone = "UTC" })))
            .Should().Throw<ValidationException>().WithMessage("*more often*");

    [Theory]
    [InlineData("")]
    [InlineData("0 9 * *")]          // four fields
    [InlineData("0 0 9 * * *")]      // six fields (seconds)
    [InlineData("99 9 * * *")]
    [InlineData("not a cron")]
    public void A_malformed_cron_is_rejected(string cron)
        => ((Action)(() => ImportScheduling.Validate(new ImportSchedule { Type = "custom", Cron = cron, TimeZone = "UTC" })))
            .Should().Throw<ValidationException>();
}

public class ImportSchedulePrepareTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_schedule_means_no_next_run()
        => ImportScheduling.Prepare(null, "{\"type\":\"daily\"}", Now).Should().Be((null, null));

    [Fact]
    public void A_daily_schedule_runs_next_at_the_chosen_time_in_its_zone()
    {
        var (schedule, next) = ImportScheduling.Prepare(
            new ImportSchedule { Type = "daily", TimeOfDay = "09:00", TimeZone = "UTC" }, null, Now);
        schedule!.AnchorOn.Should().Be(Now);
        next.Should().Be(new DateTime(2026, 10, 1, 9, 0, 0));
    }

    [Fact]
    public void A_paused_schedule_is_kept_but_never_runs()
    {
        var (schedule, next) = ImportScheduling.Prepare(
            new ImportSchedule { Enabled = false, Type = "daily", TimeOfDay = "09:00", TimeZone = "UTC" }, null, Now);
        schedule!.Enabled.Should().BeFalse();
        next.Should().BeNull();
    }

    [Fact]
    public void Editing_something_else_keeps_the_cadence_anchor()
    {
        var first = ImportScheduling.Prepare(new ImportSchedule { Type = "daily", Interval = 2, TimeOfDay = "09:00", TimeZone = "UTC" }, null, Now).Schedule!;
        var json = ImportJson.Serialize(first);
        var later = Now.AddDays(3);
        var again = ImportScheduling.Prepare(new ImportSchedule { Type = "daily", Interval = 2, TimeOfDay = "09:00", TimeZone = "utc" }, json, later).Schedule!;
        again.AnchorOn.Should().Be(first.AnchorOn);
    }

    [Fact]
    public void Changing_the_shape_restarts_the_cadence_from_now()
    {
        var first = ImportScheduling.Prepare(new ImportSchedule { Type = "daily", Interval = 2, TimeOfDay = "09:00", TimeZone = "UTC" }, null, Now).Schedule!;
        var later = Now.AddDays(3);
        var changed = ImportScheduling.Prepare(new ImportSchedule { Type = "daily", Interval = 3, TimeOfDay = "09:00", TimeZone = "UTC" },
            ImportJson.Serialize(first), later).Schedule!;
        changed.AnchorOn.Should().Be(later);
    }

    [Fact]
    public void A_client_cannot_choose_the_anchor()
    {
        var (schedule, _) = ImportScheduling.Prepare(
            new ImportSchedule { Type = "daily", TimeOfDay = "09:00", TimeZone = "UTC", AnchorOn = new DateTime(2020, 1, 1) }, null, Now);
        schedule!.AnchorOn.Should().Be(Now);
    }

    [Fact]
    public void Every_second_day_counts_from_the_anchor()
    {
        var schedule = new ImportSchedule { Type = "daily", Interval = 2, TimeOfDay = "09:00", TimeZone = "UTC", AnchorOn = Now };
        var first = ImportScheduling.NextRun(schedule, Now);
        var second = ImportScheduling.NextRun(schedule, first);
        (second - first).Should().Be(TimeSpan.FromDays(2));
    }

    [Fact]
    public void The_time_of_day_holds_across_a_daylight_saving_change()
    {
        // New York clocks go back on 1 Nov 2026: 09:00 stays 09:00 local, which is an hour later in UTC afterwards.
        var schedule = new ImportSchedule { Type = "daily", TimeOfDay = "09:00", TimeZone = "America/New_York", AnchorOn = new DateTime(2026, 10, 1) };
        ImportScheduling.NextRun(schedule, new DateTime(2026, 10, 31, 20, 0, 0)).Should().Be(new DateTime(2026, 11, 1, 14, 0, 0));
        ImportScheduling.NextRun(schedule, new DateTime(2026, 11, 1, 15, 0, 0)).Should().Be(new DateTime(2026, 11, 2, 14, 0, 0));
    }

    [Fact]
    public void A_monthly_last_day_schedule_handles_short_months()
    {
        var schedule = new ImportSchedule { Type = "monthly", MonthDays = "last", TimeOfDay = "06:00", TimeZone = "UTC", AnchorOn = new DateTime(2026, 1, 1) };
        ImportScheduling.NextRun(schedule, new DateTime(2027, 2, 1)).Should().Be(new DateTime(2027, 2, 28, 6, 0, 0));
    }
}

public class ImportScheduleDispatcherTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 9, 5, 0, DateTimeKind.Utc);
    private readonly IImportDefinitionRepository _definitions = Substitute.For<IImportDefinitionRepository>();
    private readonly IImportRunRepository _runs = Substitute.For<IImportRunRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IImportDefinitionChecker _checker = Substitute.For<IImportDefinitionChecker>();
    private readonly ImportScheduleDispatcher _sut;

    public ImportScheduleDispatcherTests()
    {
        _sut = new ImportScheduleDispatcher(_definitions, _runs, _tables, _checker);
        _definitions.TryAdvanceScheduleAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()).Returns(true);
        _tables.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(ci => new AppTable { Id = ci.Arg<long>(), PublicId = Guid.NewGuid() });
        _runs.CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>()).Returns(ci => { ci.Arg<ImportRun>().Id = 7; return 7L; });
    }

    private static ImportDefinition Due(bool enabled = true, bool needsAttention = false) => new()
    {
        Id = 11, PublicId = Guid.NewGuid(), Name = "Nightly", DestinationTableId = 2, SourceTableId = 3, RunAsUserId = 5,
        NextRunOn = new DateTime(2026, 10, 1, 9, 0, 0), NeedsAttention = needsAttention, AttentionReason = needsAttention ? "A mapped field was deleted" : null,
        ScheduleJson = ImportJson.Serialize(new ImportSchedule { Enabled = enabled, Type = "daily", TimeOfDay = "09:00", TimeZone = "UTC", AnchorOn = new DateTime(2026, 9, 1) })
    };

    [Fact]
    public async Task A_due_import_is_claimed_and_moved_to_its_next_time_counted_from_now()
    {
        var outcome = await _sut.ClaimAsync(Due(), Now, default);
        outcome.Should().Be(ImportDispatchOutcome.Claimed);
        await _definitions.Received(1).TryAdvanceScheduleAsync(11, new DateTime(2026, 10, 1, 9, 0, 0), new DateTime(2026, 10, 2, 9, 0, 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missed_occurrences_collapse_into_one_run()
    {
        // The server was down for three days: one run now, next time tomorrow, not three runs in a row.
        var def = Due(); def.NextRunOn = new DateTime(2026, 9, 28, 9, 0, 0);
        (await _sut.ClaimAsync(def, Now, default)).Should().Be(ImportDispatchOutcome.Claimed);
        await _definitions.Received(1).TryAdvanceScheduleAsync(11, new DateTime(2026, 9, 28, 9, 0, 0), new DateTime(2026, 10, 2, 9, 0, 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_another_scheduler_wins_nothing_starts()
    {
        _definitions.TryAdvanceScheduleAsync(Arg.Any<long>(), Arg.Any<DateTime>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()).Returns(false);
        (await _sut.ClaimAsync(Due(), Now, default)).Should().Be(ImportDispatchOutcome.NotClaimed);
        await _runs.DidNotReceiveWithAnyArgs().HasActiveRunAsync(default, default);
    }

    [Fact]
    public async Task A_paused_or_removed_schedule_is_cleared_and_does_not_run()
    {
        (await _sut.ClaimAsync(Due(enabled: false), Now, default)).Should().Be(ImportDispatchOutcome.Cleared);
        var removed = Due(); removed.ScheduleJson = null;
        (await _sut.ClaimAsync(removed, Now, default)).Should().Be(ImportDispatchOutcome.Cleared);
        await _definitions.Received(2).TryAdvanceScheduleAsync(11, Arg.Any<DateTime>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_run_still_going_skips_this_occurrence_but_keeps_the_schedule()
    {
        _runs.HasActiveRunAsync(11, Arg.Any<CancellationToken>()).Returns(true);
        (await _sut.ClaimAsync(Due(), Now, default)).Should().Be(ImportDispatchOutcome.SkippedOverlap);
        await _definitions.Received(1).TryAdvanceScheduleAsync(11, Arg.Any<DateTime>(), Arg.Is<DateTime?>(d => d != null), Arg.Any<CancellationToken>());
        await _runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task A_flagged_import_is_skipped_and_the_history_says_why()
    {
        (await _sut.ClaimAsync(Due(needsAttention: true), Now, default)).Should().Be(ImportDispatchOutcome.SkippedNeedsAttention);
        await _runs.Received(1).CreateAsync(Arg.Is<ImportRun>(r => r.TriggeredBy == "schedule" && r.TriggeredByUserId == 5), Arg.Any<CancellationToken>());
        await _runs.Received(1).CompleteAsync(7, ImportRunStatus.Failed, Arg.Is<string>(m => m.Contains("needs attention") && m.Contains("deleted")), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stored_schedule_that_no_longer_computes_is_cleared_instead_of_retried_every_minute()
    {
        var def = Due();
        def.ScheduleJson = ImportJson.Serialize(new ImportSchedule { Type = "custom", Cron = "garbage", TimeZone = "UTC" });
        (await _sut.ClaimAsync(def, Now, default)).Should().Be(ImportDispatchOutcome.Claimed);
        await _definitions.Received(1).TryAdvanceScheduleAsync(11, Arg.Any<DateTime>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_recorded_failure_can_be_opened_like_any_run()
    {
        await _sut.RecordFailedRunAsync(Due(), new string('x', 2000), default);
        // A real destination table id in the snapshot is what lets the run page pass its access check.
        await _runs.Received(1).CreateAsync(
            Arg.Is<ImportRun>(r => ImportJson.Deserialize<ImportRunSnapshot>(r.DefinitionSnapshotJson)!.DestinationTableId != Guid.Empty), Arg.Any<CancellationToken>());
        await _runs.Received(1).CompleteAsync(7, ImportRunStatus.Failed, Arg.Is<string>(m => m.Length == 900), null, Arg.Any<CancellationToken>());
    }
}
