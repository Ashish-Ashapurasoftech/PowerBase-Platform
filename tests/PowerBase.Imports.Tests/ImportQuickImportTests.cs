using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>An import started from a table's own menu has no template to keep: it is saved and started through the template handlers and the definition is removed
/// again, whatever happens to the start. The run itself is the one a template makes.</summary>
public class ImportQuickImportTests
{
    private sealed class Quick
    {
        public IImportDefinitionRepository Definitions { get; } = Substitute.For<IImportDefinitionRepository>();
        public IImportRunRepository Runs { get; } = Substitute.For<IImportRunRepository>();
        public IImportFileAccess Access { get; } = Substitute.For<IImportFileAccess>();
        public IAppAccessService AppAccess { get; } = Substitute.For<IAppAccessService>();
        public IImportQueue Queue { get; } = Substitute.For<IImportQueue>();
        public ImportFile Upload { get; }
        public ImportDefinition? Created { get; private set; }
        public StartQuickImportHandler Handler { get; }

        public Quick(long uploader = 5)
        {
            var h = ImportHarness.Create(FileConfig());
            var tables = Substitute.For<IAppTableRepository>();
            tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 1, PublicId = ImportHarness.DestTableId });
            var people = new AppTable { Id = 10, AppId = 1, PublicId = ImportHarness.SourceTableId, Name = "People" };
            tables.GetByPublicIdAsync(ImportHarness.SourceTableId, Arg.Any<CancellationToken>()).Returns(people);
            tables.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(people);

            Definitions.CreateAsync(Arg.Any<ImportDefinition>(), Arg.Any<CancellationToken>()).Returns(ci =>
            {
                Created = ci.Arg<ImportDefinition>();
                Created.PublicId = Guid.NewGuid();
                Created.Id = 77;
                return 77L;
            });
            Definitions.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci => Created is not null && Created.PublicId == ci.Arg<Guid>() ? Created : null);

            var files = Substitute.For<IImportFileRepository>();
            Upload = new ImportFile { PublicId = Guid.NewGuid(), UploadedByUserId = uploader, Format = "csv", StoragePath = "/files/x", FileName = "students.csv" };
            files.GetByPublicIdAsync(Upload.PublicId, Arg.Any<CancellationToken>()).Returns(Upload);
            Access.OpenSourceAsync(Arg.Any<ImportRunSnapshot>(), false, Arg.Any<CancellationToken>()).Returns(ci =>
            {
                var options = ci.Arg<ImportRunSnapshot>().Config.File!;
                return new ImportFileSource(ImportFileColumns.FromHeader(options.Columns.ToArray(), options.Columns.Count), "students.csv", options.ToLayout(), "/files/x", 0, 0, options);
            });
            Runs.CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>()).Returns(ci => { ci.Arg<ImportRun>().PublicId = Guid.NewGuid(); return 9L; });

            var user = Substitute.For<IQueryContext>();
            user.UserId.Returns(5L);
            user.TenantId.Returns(1L);
            var save = new SaveImportDefinitionHandler(h.PlanBuilder, tables, Definitions, user);
            var start = new StartImportRunHandler(h.PlanBuilder, tables, Substitute.For<IAppRepository>(), Definitions, Runs, Queue, user,
                Substitute.For<IImportDefinitionChecker>(), files, Access);
            Handler = new StartQuickImportHandler(save, start, Definitions, AppAccess, tables, user, NullLogger<StartQuickImportHandler>.Instance);
        }

        public Task<ImportRunStart> Run(ImportDefinitionConfig cfg, Guid? fileId = null, IReadOnlyList<string>? notify = null) =>
            Handler.HandleAsync(ImportHarness.DestTableId, new QuickImportRequest(cfg, fileId ?? Upload.PublicId, notify), "https://app.example", default);
    }

    private static ImportDefinitionConfig FileConfig(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "students.csv", SourceKind = ImportSourceKinds.File,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Qty"] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    [Fact]
    public async Task A_run_is_queued_like_a_templates_and_the_definition_that_carried_it_is_removed()
    {
        var q = new Quick();

        var started = await q.Run(FileConfig(), notify: ["a@b.co"]);

        started.RunId.Should().NotBeEmpty();
        await q.Runs.Received(1).CreateAsync(Arg.Is<ImportRun>(r => r.ImportDefinitionId == 77 && r.TriggeredBy == ImportTrigger.Manual), Arg.Any<CancellationToken>());
        await q.Queue.Received(1).EnqueueAsync(1, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await q.Definitions.Received(1).DeleteAsync(77, 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_run_is_named_for_what_was_imported_from()
    {
        var q = new Quick();

        await q.Run(FileConfig());

        q.Created!.Name.Should().Be("Quick import - students.csv");
        await q.Runs.Received(1).CreateAsync(Arg.Is<ImportRun>(r => r.DefinitionSnapshotJson.Contains("Quick import - students.csv")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_table_source_is_named_for_the_table()
    {
        var q = new Quick();
        var cfg = new ImportDefinitionConfig
        {
            Name = "", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }]
        };

        await q.Handler.HandleAsync(ImportHarness.DestTableId, new QuickImportRequest(cfg, null, null), null, default);

        q.Created!.Name.Should().Be("Quick import - People");
    }

    [Fact]
    public async Task A_start_that_fails_still_removes_the_definition_and_shows_the_reason()
    {
        var q = new Quick();
        q.Access.OpenSourceAsync(Arg.Any<ImportRunSnapshot>(), false, Arg.Any<CancellationToken>())
            .Returns<Task<ImportFileSource>>(_ => throw new ImportFileFormatException("Row 1, which should hold the column names, is not in the file."));

        var act = () => q.Run(FileConfig());

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*column names*");
        await q.Definitions.Received(1).DeleteAsync(77, 5, Arg.Any<CancellationToken>());
        await q.Runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task A_definition_that_cannot_be_removed_does_not_hide_the_run()
    {
        var q = new Quick();
        q.Definitions.DeleteAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new InvalidOperationException("db down"));

        var started = await q.Run(FileConfig());

        started.RunId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_bad_mapping_is_refused_by_the_same_checks_as_a_saved_import_and_nothing_is_saved()
    {
        var q = new Quick();

        var act = () => q.Run(FileConfig(c => c.Mappings = []));

        await act.Should().ThrowAsync<ValidationException>();
        q.Created.Should().BeNull();
        await q.Runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Only_a_member_of_the_tables_app_may_import()
    {
        var q = new Quick();
        q.AppAccess.RequireMembershipByTablePublicIdAsync(ImportHarness.DestTableId, Arg.Any<CancellationToken>()).Returns(Task.FromException(new UnauthorizedActionException("read the app")));

        var act = () => q.Run(FileConfig());

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await q.Definitions.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task What_only_a_kept_template_can_do_is_refused()
    {
        var q = new Quick();

        await ((Func<Task>)(() => q.Run(FileConfig(c => c.Schedule = new ImportSchedule { Enabled = true, Type = "hourly", Interval = 1 })))).Should().ThrowAsync<ValidationException>().WithMessage("*scheduled*");
        await ((Func<Task>)(() => q.Run(FileConfig(c => c.OnlyChangedSinceLastRun = true)))).Should().ThrowAsync<ValidationException>().WithMessage("*saved imports*");
        q.Created.Should().BeNull();
    }

    [Fact]
    public async Task A_one_time_import_takes_what_a_template_takes_variables_a_table_filter_and_further_tables()
    {
        var q = new Quick();

        // Variables and a table's own filter go through the same save checks and start a run.
        var filter = new PowerBase.Application.Reports.FilterGroup { Nodes = [new() { Condition = new() { FieldId = 1001, Operator = "contains", Value = "{{var_part}}" } }] };
        var start = await q.Run(FileConfig(c =>
        {
            c.Variables = [new ImportVariable { Name = "var_part", Value = "a" }];
            c.TableConditions = filter;
        }));
        start.RunId.Should().NotBeEmpty();
        q.Created!.OptionsJson.Should().Contain("var_part");

        // Another table is no longer refused up front: the plan builder judges it (here the table does not exist).
        var act = () => q.Run(FileConfig(c => c.AdditionalTargets = [new ImportTargetConfig { DestinationTableId = Guid.NewGuid() }]));
        var ex = await Record.ExceptionAsync(act);
        ex?.Message.Should().NotContain("this table only");
    }

    [Fact]
    public async Task A_one_time_merge_can_match_on_the_record_id_and_is_saved_and_started_like_a_template_would()
    {
        var q = new Quick();
        var cfg = FileConfig(c =>
        {
            c.ImportType = ImportTypes.Merge;
            c.MergeKeyFid = 3;
            c.File!.Columns = ["Record ID", "Qty"];
            c.Mappings = [new() { DestFid = 3, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }];
        });

        var start = await q.Run(cfg);

        start.RunId.Should().NotBeEmpty();
        q.Created!.MergeKeyFid.Should().Be(3);
        q.Created.ImportType.Should().Be(ImportTypes.Merge);
    }

    [Fact]
    public async Task A_one_time_merge_on_the_record_id_without_a_column_for_it_is_refused_before_anything_starts()
    {
        var q = new Quick();
        var cfg = FileConfig(c => { c.ImportType = ImportTypes.Merge; c.MergeKeyFid = 3; });

        var act = () => q.Run(cfg);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Record ID#*");
        q.Created.Should().BeNull("a refused setup leaves no definition behind");
    }

    [Fact]
    public async Task A_one_time_import_with_a_formula_variable_is_saved_with_it_and_started()
    {
        var q = new Quick();
        var cfg = FileConfig(c =>
        {
            c.Variables = [new ImportVariable { Name = "var_limit", Kind = ImportVariables.Formula, Formula = "2 + 3" }];
            c.Mappings.Add(new ImportFieldMapping { DestFid = 9, Source = ImportMappingSource.Formula, Formula = "ToText([var_limit] * 2)" });
        });

        var start = await q.Run(cfg);

        start.RunId.Should().NotBeEmpty();
        q.Created!.OptionsJson.Should().Contain("var_limit").And.Contain("formula");
    }

    [Fact]
    public async Task A_file_import_needs_a_file_and_a_table_import_takes_none()
    {
        var q = new Quick();

        await ((Func<Task>)(() => q.Handler.HandleAsync(ImportHarness.DestTableId, new QuickImportRequest(FileConfig(), null, null), null, default)))
            .Should().ThrowAsync<ValidationException>().WithMessage("*Choose the file*");
        var table = new ImportDefinitionConfig { SourceTableId = ImportHarness.SourceTableId, Mappings = [new() { DestFid = 6, SourceFid = 6 }] };
        await ((Func<Task>)(() => q.Handler.HandleAsync(ImportHarness.DestTableId, new QuickImportRequest(table, Guid.NewGuid(), null), null, default)))
            .Should().ThrowAsync<ValidationException>().WithMessage("*reads a table*");
    }

    [Fact]
    public async Task Another_users_upload_is_not_found_and_the_definition_is_still_removed()
    {
        var q = new Quick(uploader: 99);

        var act = () => q.Run(FileConfig());

        await act.Should().ThrowAsync<NotFoundException>();
        await q.Definitions.Received(1).DeleteAsync(77, 5, Arg.Any<CancellationToken>());
    }
}
