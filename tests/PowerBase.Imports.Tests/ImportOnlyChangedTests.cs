using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>"Only records changed since the last run": a merge from a table that reads only what was created or changed after its last finished run, so a
/// scheduled import does not read, compare and rewrite records nothing has touched.</summary>
public class ImportOnlyChangedTests
{
    private static readonly DateTime LastStart = new(2026, 10, 14, 6, 0, 0, DateTimeKind.Utc);

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Project replica", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Merge, MergeKeyFid = 6, OnlyChangedSinceLastRun = true,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    private static string Snapshot(ImportDefinitionConfig config) => ImportJson.Serialize(new ImportRunSnapshot(ImportHarness.DestTableId, config, null, null));

    // ---- when the records may be skipped ----

    [Fact]
    public void Without_the_option_or_a_finished_run_every_record_is_read()
    {
        var cfg = Config();

        ImportRunSince.Since(Config(c => c.OnlyChangedSinceLastRun = false), (LastStart, Snapshot(cfg))).Should().BeNull();
        ImportRunSince.Since(cfg, null).Should().BeNull("the first run reads everything");
    }

    [Fact]
    public void After_a_finished_run_the_records_are_read_from_a_little_before_that_run_started()
    {
        var cfg = Config();

        var since = ImportRunSince.Since(cfg, (LastStart, Snapshot(cfg)));

        since.Should().Be(LastStart - ImportRunSince.Margin);
        since!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void An_import_edited_since_the_last_run_reads_everything_once_because_its_new_mapping_has_not_met_the_unchanged_records()
    {
        var before = Config();
        var edited = Config(c => c.Mappings.Add(new() { DestFid = 9, SourceFid = 9 }));

        ImportRunSince.Since(edited, (LastStart, Snapshot(before))).Should().BeNull();
        ImportRunSince.Since(Config(c => c.MergeKeyFid = 7), (LastStart, Snapshot(before))).Should().BeNull("another key");
        ImportRunSince.Since(Config(c => c.ColumnRules = [new() { DestFid = 6, RequireField = true }]), (LastStart, Snapshot(before))).Should().BeNull("another rule");
    }

    [Fact]
    public void Who_is_told_the_schedule_and_the_name_do_not_make_an_import_changed()
    {
        var before = Config();
        var same = Config(c =>
        {
            c.Name = "Renamed";
            c.NotifyEmails = ["someone@example.com"];
            c.Schedule = new ImportSchedule { Enabled = true, Type = "hourly", Interval = 1 };
        });

        ImportRunSince.Since(same, (LastStart, Snapshot(before))).Should().Be(LastStart - ImportRunSince.Margin);
        ImportRunSince.Fingerprint(before).Should().Be(ImportRunSince.Fingerprint(same));
    }

    [Fact]
    public void A_snapshot_that_cannot_be_read_reads_everything()
    {
        ImportRunSince.Since(Config(), (LastStart, "{ not json")).Should().BeNull();
    }

    // ---- what the run reads ----

    private static ImportHarness Harness(ImportDefinitionConfig cfg) => ImportHarness.Create(cfg, new HarnessOptions
    {
        // The two system fields every table has: Date Created and Date Modified.
        ConfigureSource = fields =>
        {
            var created = ImportHarness.Field(10, 1, "Date Created", "DateTime");
            created.IsSystem = true;
            var modified = ImportHarness.Field(10, 2, "Date Modified", "DateTime");
            modified.IsSystem = true;
            fields.AddRange([created, modified]);
        }
    });

    [Fact]
    public async Task The_run_reads_only_records_created_or_changed_after_the_moment_and_the_users_own_conditions_still_apply()
    {
        var since = LastStart - ImportRunSince.Margin;
        var h = Harness(Config(c => c.ChangedSinceUtc = since));

        var plan = (await h.PlanBuilder!.BuildAllAsync(Config(c => c.ChangedSinceUtc = since), ImportHarness.DestTableId, default)).Single();

        var filter = JsonSerializer.Serialize(plan.SourceFilter);
        filter.Should().Contain("\"or\"", "changed OR created, since a record never changed has no change time").And.Contain(since.ToString("O"));
        filter.Should().Contain("\"FieldId\":2").And.Contain("\"FieldId\":1").And.Contain("\"gt\"");
    }

    [Fact]
    public async Task With_no_moment_the_run_reads_everything_as_before()
    {
        var h = Harness(Config());

        var plan = (await h.PlanBuilder!.BuildAllAsync(Config(), ImportHarness.DestTableId, default)).Single();

        plan.SourceFilter.Should().BeNull();
    }

    // ---- who may use it ----

    [Fact]
    public async Task Only_a_merge_from_a_table_can_read_only_what_changed()
    {
        var copy = Config(c => c.ImportType = ImportTypes.Copy);
        var act = () => Harness(copy).PlanBuilder!.BuildAllAsync(copy, ImportHarness.DestTableId, default);
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*merge from a table*");

        var file = Config(c => { c.SourceKind = ImportSourceKinds.File; c.File = new ImportFileOptions { Format = "csv", Columns = ["Name"] }; });
        var viaFile = () => Harness(file).PlanBuilder!.BuildAllAsync(file, ImportHarness.DestTableId, default,
            ImportFileValidation.SavedSource(file.File!));
        await viaFile.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task A_table_cannot_read_only_what_changed_from_itself_because_every_run_changes_its_own_records()
    {
        var cfg = Config(c => c.Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }]);
        var h = ImportHarness.Create(cfg, new HarnessOptions { SameTable = true });

        var act = () => h.PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*source is the table being filled*");
    }

    [Fact]
    public async Task Every_table_an_import_fills_must_merge_or_the_changed_records_would_be_added_again()
    {
        var extra = Guid.NewGuid();
        var cfg = Config(c => c.AdditionalTargets = [new ImportTargetConfig { DestinationTableId = extra, ImportType = ImportTypes.Copy, Mappings = [new() { DestFid = 6, SourceFid = 6 }] }]);

        var h = ImportHarness.Create(cfg, new HarnessOptions { ExtraDestinations = [new HarnessDestination { Id = 12, PublicId = extra, Name = "Other", Fields = [ImportHarness.RecordId(12), ImportHarness.Field(12, 6, "Name", "Text")] }] });
        var act = () => h.PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Other*merge from a table*");
    }

    // ---- saved and read back ----

    [Fact]
    public void The_option_is_saved_with_the_import_and_an_import_without_it_reads_everything_as_it_always_did()
    {
        var saved = new ImportDefinition();
        ImportConfigMapper.Apply(saved, Config());

        ImportConfigMapper.ToConfig(saved, Guid.Empty).OnlyChangedSinceLastRun.Should().BeTrue();

        var plain = new ImportDefinition();
        ImportConfigMapper.Apply(plain, Config(c => c.OnlyChangedSinceLastRun = false));
        ImportConfigMapper.ToConfig(plain, Guid.Empty).OnlyChangedSinceLastRun.Should().BeFalse();
        plain.OptionsJson.Should().BeNull("an import that does not use it stores nothing new");
    }

    [Fact]
    public void The_moment_of_a_run_is_not_saved_with_the_import()
    {
        var saved = new ImportDefinition();
        ImportConfigMapper.Apply(saved, Config(c => c.ChangedSinceUtc = LastStart));

        saved.OptionsJson.Should().NotContain("changedSince");
    }

    // ---- the run asks for it when it is started ----

    [Fact]
    public async Task A_run_is_queued_with_the_moment_from_the_last_finished_run()
    {
        var snapshot = await StartWith(lastFinished: (LastStart, null));

        snapshot.Config.ChangedSinceUtc.Should().Be(LastStart - ImportRunSince.Margin);
    }

    [Theory]
    [InlineData(ImportTrigger.Manual)]
    [InlineData(ImportTrigger.Api)]
    public async Task A_run_started_by_a_person_or_the_api_reads_every_record(string trigger)
    {
        (await StartWith(lastFinished: (LastStart, null), trigger)).Config.ChangedSinceUtc.Should().BeNull();
    }

    [Fact]
    public async Task The_first_run_and_a_run_after_an_edit_read_everything()
    {
        (await StartWith(lastFinished: null)).Config.ChangedSinceUtc.Should().BeNull();
        (await StartWith(lastFinished: (LastStart, Config(c => c.Mappings.Add(new() { DestFid = 9, SourceFid = 9 }))))).Config.ChangedSinceUtc.Should().BeNull();
    }

    private async Task<ImportRunSnapshot> StartWith((DateTime StartedOn, ImportDefinitionConfig? Before)? lastFinished, string trigger = ImportTrigger.Schedule)
    {
        var cfg = Config();
        var h = Harness(cfg);
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 1, PublicId = ImportHarness.DestTableId });
        tables.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10, AppId = 1, PublicId = ImportHarness.SourceTableId });
        var definition = new ImportDefinition { Id = 1, PublicId = Guid.NewGuid(), DestinationTableId = 11, SourceTableId = 10 };
        ImportConfigMapper.Apply(definition, cfg);
        var definitions = Substitute.For<IImportDefinitionRepository>();
        definitions.GetByPublicIdAsync(definition.PublicId, Arg.Any<CancellationToken>()).Returns(definition);

        var runs = Substitute.For<IImportRunRepository>();
        if (lastFinished is { } last)
        {
            var before = last.Before ?? ImportConfigMapper.ToConfig(definition, ImportHarness.SourceTableId);
            runs.GetLastFinishedRunAsync(1, Arg.Any<CancellationToken>()).Returns((last.StartedOn, Snapshot(before)));
        }
        ImportRun? created = null;
        runs.When(r => r.CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>())).Do(c => { created = c.ArgAt<ImportRun>(0); created.PublicId = Guid.NewGuid(); });
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(5L);
        user.TenantId.Returns(1L);
        var handler = new StartImportRunHandler(h.PlanBuilder!, tables, Substitute.For<IAppRepository>(), definitions, runs, Substitute.For<IImportQueue>(), user,
            Substitute.For<IImportDefinitionChecker>(), Substitute.For<IImportFileRepository>(), Substitute.For<IImportFileAccess>());

        await handler.HandleAsync(definition.PublicId, trigger, null, null, default);

        return ImportJson.Deserialize<ImportRunSnapshot>(created!.DefinitionSnapshotJson)!;
    }
}
