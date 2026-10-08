using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>Saving, opening, starting and checking an import that fills several tables, and how its run is shown and announced.</summary>
public class ImportMultiTargetHandlerTests
{
    private const long ContactsId = 12;
    private static readonly Guid ContactsPublicId = Guid.NewGuid();

    private static HarnessDestination Contacts() => new()
    {
        Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts",
        Fields = [ImportHarness.RecordId(ContactsId), ImportHarness.Field(ContactsId, 6, "Title", "Text"), ImportHarness.Field(ContactsId, 8, "Code", "Text", unique: true)]
    };

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Two tables", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }],
            AdditionalTargets = [new ImportTargetConfig { DestinationTableId = ContactsPublicId, Mappings = [new() { DestFid = 6, SourceFid = 9 }, new() { DestFid = 8, SourceFid = 6 }] }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    private static ImportHarness Harness() => ImportHarness.Create(Config(), new HarnessOptions { ExtraDestinations = [Contacts()] });

    [Fact]
    public void A_client_sending_null_for_the_extra_tables_or_a_tables_lists_means_none_not_a_crash()
    {
        var cfg = ImportJson.Deserialize<ImportDefinitionConfig>(
            "{\"name\":\"n\",\"additionalTargets\":null,\"mappings\":[]}")!;
        cfg.AdditionalTargets.Should().BeEmpty();

        var withTarget = ImportJson.Deserialize<ImportDefinitionConfig>(
            "{\"name\":\"n\",\"additionalTargets\":[{\"destinationTableId\":\"" + Guid.NewGuid() + "\",\"mappings\":null,\"columnRules\":null}]}")!;
        withTarget.AdditionalTargets.Single().Mappings.Should().BeEmpty();
        withTarget.AdditionalTargets.Single().ColumnRules.Should().BeEmpty();
    }

    // ---- save ----

    private static (SaveImportDefinitionHandler Handler, IImportDefinitionRepository Definitions) Saver(ImportHarness h)
    {
        var definitions = Substitute.For<IImportDefinitionRepository>();
        definitions.CreateAsync(Arg.Any<ImportDefinition>(), Arg.Any<CancellationToken>()).Returns(1L);
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(5L);
        return (new SaveImportDefinitionHandler(h.PlanBuilder!, Substitute.For<IAppTableRepository>(), definitions, user), definitions);
    }

    [Fact]
    public async Task Saving_keeps_every_table_in_the_stored_row_and_the_import_lives_under_its_own_table()
    {
        var (sut, definitions) = Saver(Harness());

        await sut.HandleAsync(ImportHarness.DestTableId, null, Config(), default);

        await definitions.Received(1).CreateAsync(Arg.Is<ImportDefinition>(d =>
            d.DestinationTableId == 11 && d.OptionsJson!.Contains("additionalTargets") && d.OptionsJson.Contains(ContactsPublicId.ToString())), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_problem_in_an_extra_table_is_refused_when_saving_and_names_the_table()
    {
        var (sut, definitions) = Saver(Harness());
        var cfg = Config(c => c.AdditionalTargets[0].Mappings = [new() { DestFid = 99, SourceFid = 9 }]);

        var act = () => sut.HandleAsync(ImportHarness.DestTableId, null, cfg, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*'Contacts'*");
        await definitions.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    // ---- open and list ----

    [Fact]
    public async Task Opening_an_import_returns_its_extra_tables_only_when_it_has_some()
    {
        var def = new ImportDefinition { Id = 1, PublicId = Guid.NewGuid(), DestinationTableId = 11, SourceTableId = 10, Name = "n" };
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, PublicId = ImportHarness.DestTableId });
        tables.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10, PublicId = ImportHarness.SourceTableId });
        var definitions = Substitute.For<IImportDefinitionRepository>();
        definitions.GetByPublicIdAsync(def.PublicId, Arg.Any<CancellationToken>()).Returns(def);
        var sut = new GetImportDefinitionHandler(tables, Substitute.For<IAppAccessService>(), definitions, Substitute.For<IImportDefinitionChecker>());

        ImportConfigMapper.Apply(def, Config());
        (await sut.HandleAsync(def.PublicId, default)).AdditionalTargets.Should().ContainSingle().Which.DestinationTableId.Should().Be(ContactsPublicId);

        ImportConfigMapper.Apply(def, Config(c => c.AdditionalTargets = []));
        (await sut.HandleAsync(def.PublicId, default)).AdditionalTargets.Should().BeNull("an import into one table sends nothing extra");
    }

    // ---- start ----

    private sealed class Start
    {
        public IImportRunRepository Runs { get; } = Substitute.For<IImportRunRepository>();
        public StartImportRunHandler Handler { get; }
        public ImportDefinition Definition { get; }

        public Start(Action<ImportDefinitionConfig>? tweak = null)
        {
            var h = ImportHarness.Create(Config(), new HarnessOptions { ExtraDestinations = [Contacts()] });
            var tables = Substitute.For<IAppTableRepository>();
            tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 1, PublicId = ImportHarness.DestTableId });
            tables.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10, AppId = 1, PublicId = ImportHarness.SourceTableId });
            Definition = new ImportDefinition { Id = 1, PublicId = Guid.NewGuid(), DestinationTableId = 11, SourceTableId = 10, Name = "Two tables", ImportType = "copy" };
            ImportConfigMapper.Apply(Definition, Config(tweak));
            var definitions = Substitute.For<IImportDefinitionRepository>();
            definitions.GetByPublicIdAsync(Definition.PublicId, Arg.Any<CancellationToken>()).Returns(Definition);
            Runs.CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>()).Returns(ci => { ci.Arg<ImportRun>().PublicId = Guid.NewGuid(); return 9L; });
            var user = Substitute.For<IQueryContext>();
            user.UserId.Returns(5L);
            user.TenantId.Returns(1L);
            Handler = new StartImportRunHandler(h.PlanBuilder!, tables, Substitute.For<IAppRepository>(), definitions, Runs, Substitute.For<IImportQueue>(), user,
                Substitute.For<IImportDefinitionChecker>(), Substitute.For<IImportFileRepository>(), Substitute.For<IImportFileAccess>());
        }

        public Task<Guid> Run() => Handler.HandleAsync(Definition.PublicId, ImportTrigger.Manual, null, null, default);
    }

    [Fact]
    public async Task A_run_remembers_every_table_it_will_fill()
    {
        var s = new Start();

        await s.Run();

        await s.Runs.Received(1).CreateAsync(Arg.Is<ImportRun>(r =>
            ImportJson.Deserialize<ImportRunSnapshot>(r.DefinitionSnapshotJson)!.Config.AdditionalTargets.Count == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_run_is_refused_before_it_is_queued_when_an_extra_table_has_a_problem()
    {
        var s = new Start(c => c.AdditionalTargets[0].Mappings = [new() { DestFid = 99, SourceFid = 9 }]);

        var act = () => s.Run();

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*'Contacts'*");
        await s.Runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    // ---- needs attention ----

    private sealed class Checker
    {
        public List<AppField> Home { get; } = [ImportHarness.RecordId(11), ImportHarness.Field(11, 6, "Name", "Text", unique: true), ImportHarness.Field(11, 7, "Qty", "Number")];
        public List<AppField> Source { get; } = [ImportHarness.RecordId(10), ImportHarness.Field(10, 6, "Name", "Text"), ImportHarness.Field(10, 8, "Qty", "Text"), ImportHarness.Field(10, 9, "Note", "Text")];
        public List<AppField> Other { get; } = Contacts().Fields;
        public IAppTableRepository Tables { get; } = Substitute.For<IAppTableRepository>();
        public ImportDefinitionChecker Sut { get; }
        public ImportDefinition Def { get; }

        public Checker()
        {
            var fields = Substitute.For<IAppFieldRepository>();
            Tables.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10, PublicId = ImportHarness.SourceTableId });
            Tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, PublicId = ImportHarness.DestTableId, Name = "Dst" });
            Tables.GetByPublicIdAsync(ContactsPublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts" });
            Tables.GetByIdAsync(ContactsId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts" });
            fields.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(_ => Source);
            fields.ListByTableAsync(11, Arg.Any<CancellationToken>()).Returns(_ => Home);
            fields.ListByTableAsync(ContactsId, Arg.Any<CancellationToken>()).Returns(_ => Other);
            Sut = new ImportDefinitionChecker(Tables, fields, new PowerBase.Formula.FormulaEngine(), Substitute.For<IImportDefinitionRepository>());
            Def = new ImportDefinition { Id = 1, DestinationTableId = 11, SourceTableId = 10, ImportType = "copy" };
            ImportConfigMapper.Apply(Def, Config());
        }
    }

    [Fact]
    public async Task A_sound_multi_table_import_is_not_flagged()
    {
        var c = new Checker();

        await c.Sut.RefreshAsync(c.Def, default);

        c.Def.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public async Task A_field_deleted_in_an_extra_table_flags_the_import_and_names_the_table()
    {
        var c = new Checker();
        c.Other.RemoveAll(f => f.Fid == 8);

        await c.Sut.RefreshAsync(c.Def, default);

        c.Def.NeedsAttention.Should().BeTrue();
        c.Def.AttentionReason.Should().StartWith("'Contacts':").And.Contain("deleted");
    }

    [Fact]
    public async Task An_extra_table_that_is_gone_flags_the_import()
    {
        var c = new Checker();
        c.Tables.GetByPublicIdAsync(ContactsPublicId, Arg.Any<CancellationToken>()).Returns<AppTable>(_ => throw new NotFoundException("Table", ContactsPublicId));

        await c.Sut.RefreshAsync(c.Def, default);

        c.Def.NeedsAttention.Should().BeTrue();
        c.Def.AttentionReason.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task A_field_deleted_in_the_home_table_still_flags_the_import_as_before()
    {
        var c = new Checker();
        c.Home.RemoveAll(f => f.Fid == 7);

        await c.Sut.RefreshAsync(c.Def, default);

        c.Def.NeedsAttention.Should().BeTrue();
        c.Def.AttentionReason.Should().NotStartWith("'Contacts'");
    }

    [Fact]
    public async Task An_extra_tables_merge_key_that_stops_being_unique_flags_the_import()
    {
        var c = new Checker();
        ImportConfigMapper.Apply(c.Def, Config(x => { x.AdditionalTargets[0].ImportType = ImportTypes.Merge; x.AdditionalTargets[0].MergeKeyFid = 8; }));
        await c.Sut.RefreshAsync(c.Def, default);
        c.Def.NeedsAttention.Should().BeFalse();

        c.Other.Single(f => f.Fid == 8).IsUnique = false;
        await c.Sut.RefreshAsync(c.Def, default);

        c.Def.AttentionReason.Should().StartWith("'Contacts':").And.Contain("not unique");
    }

    // ---- the run page and the email ----

    [Fact]
    public async Task The_run_page_shows_each_tables_counts_for_a_multi_table_run_and_nothing_extra_for_a_single_one()
    {
        var runs = Substitute.For<IImportRunRepository>();
        ImportRun Run(ImportDefinitionConfig cfg) => new()
        {
            Id = 3, PublicId = Guid.NewGuid(), Status = "partial", TriggeredByUserId = 5,
            DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(ImportHarness.DestTableId, cfg))
        };
        var multi = Run(Config());
        var single = Run(Config(c => c.AdditionalTargets = []));
        runs.GetByPublicIdAsync(multi.PublicId, Arg.Any<CancellationToken>()).Returns(multi);
        runs.GetByPublicIdAsync(single.PublicId, Arg.Any<CancellationToken>()).Returns(single);
        runs.ListTargetsAsync(3, Arg.Any<CancellationToken>()).Returns([new ImportRunTargetItem(ImportHarness.DestTableId, "Dst", 10, 0, 1, 0)]);
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(5L);
        var sut = new GetImportRunHandler(Substitute.For<IAppAccessService>(), runs, user);

        (await sut.HandleAsync(multi.PublicId, default)).Targets.Should().ContainSingle().Which.TableName.Should().Be("Dst");
        runs.ClearReceivedCalls();
        (await sut.HandleAsync(single.PublicId, default)).Targets.Should().BeNull();
        await runs.DidNotReceiveWithAnyArgs().ListTargetsAsync(default, default);
    }

    private static async Task<string> EmailBodyAsync(ImportDefinitionConfig cfg, IReadOnlyList<ImportRunTargetItem>? targets)
    {
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(new User { Id = 5, Email = "boss@example.com" });
        var email = Substitute.For<IEmailService>();
        string? body = null;
        await email.SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<string>(b => body = b), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        var runs = Substitute.For<IImportRunRepository>();
        runs.ListTargetsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(targets ?? []);
        var notifier = new ImportCompletionNotifier(users, Substitute.For<IAppTableRepository>(), Substitute.For<IAppRepository>(), email,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportCompletionNotifier>.Instance, runs);
        var run = new ImportRun { Id = 3, Status = "partial", RowsRead = 50, Inserted = 90, Skipped = 3, Errored = 2, TriggeredByUserId = 5 };

        await notifier.NotifyAsync(run, new ImportRunSnapshot(ImportHarness.DestTableId, cfg), default);

        return body ?? "";
    }

    [Fact]
    public async Task The_email_lists_each_tables_counts_and_a_single_table_email_is_as_before()
    {
        var targets = new[] { new ImportRunTargetItem(Guid.NewGuid(), "Students", 48, 0, 1, 1), new ImportRunTargetItem(Guid.NewGuid(), "Contacts <b>", 42, 0, 2, 1) };

        var multi = await EmailBodyAsync(Config(), targets);
        var single = await EmailBodyAsync(Config(c => c.AdditionalTargets = []), targets);

        multi.Should().Contain("By table").And.Contain("Students").And.Contain("48 imported, 1 skipped, 1 errors");
        multi.Should().Contain("Contacts &lt;b&gt;", "a table name cannot add markup to the email");
        single.Should().NotContain("By table", "a single-table import never asks for per-table counts");
    }
}
