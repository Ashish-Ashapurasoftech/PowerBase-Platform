using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

public class ImportFileValidationTests
{
    private static ImportFileOptions Valid() => new() { Format = "csv", HeaderRow = 1, Columns = ["Name", "Qty"] };

    private static Action Check(Action<ImportFileOptions> tweak)
    {
        var o = Valid();
        tweak(o);
        return () => ImportFileValidation.Validate(o);
    }

    [Fact]
    public void Valid_settings_are_accepted_and_cleaned()
    {
        var o = ImportFileValidation.Validate(new ImportFileOptions { Format = "CSV ", Sheet = "  ", Columns = [" Name ", "Qty"], Delimiter = "" });

        o.Format.Should().Be("csv");
        o.Sheet.Should().BeNull();
        o.Delimiter.Should().BeNull();
        o.Columns.Should().Equal("Name", "Qty");
    }

    [Fact] public void Missing_settings_are_refused() => ((Action)(() => ImportFileValidation.Validate(null))).Should().Throw<ValidationException>();
    [Fact] public void An_unknown_type_is_refused() => Check(o => o.Format = "pdf").Should().Throw<ValidationException>();
    [Fact] public void A_header_row_out_of_range_is_refused() => Check(o => o.HeaderRow = 5000).Should().Throw<ValidationException>();
    [Fact] public void A_negative_header_row_is_refused() => Check(o => o.HeaderRow = -1).Should().Throw<ValidationException>();
    [Fact] public void No_header_is_allowed() => Check(o => o.HeaderRow = 0).Should().NotThrow();
    [Fact] public void Data_cannot_start_on_or_above_the_header() => Check(o => o.DataStartRow = 1).Should().Throw<ValidationException>();
    [Fact] public void Data_can_start_below_the_header() => Check(o => o.DataStartRow = 3).Should().NotThrow();
    [Fact] public void Data_cannot_start_at_row_zero() => Check(o => { o.HeaderRow = 0; o.DataStartRow = 0; }).Should().Throw<ValidationException>();
    [Theory]
    [InlineData("ab")]
    [InlineData("\"")]
    [InlineData("\n")]
    public void A_bad_separator_is_refused(string delimiter) => Check(o => o.Delimiter = delimiter).Should().Throw<ValidationException>();
    [Theory]
    [InlineData(";")]
    [InlineData("\\t")]
    [InlineData("|")]
    public void Real_separators_are_accepted(string delimiter) => Check(o => o.Delimiter = delimiter).Should().NotThrow();
    [Fact] public void An_unknown_way_of_finding_columns_is_refused() => Check(o => o.ColumnBy = "magic").Should().Throw<ValidationException>();
    [Fact] public void An_import_needs_at_least_one_column() => Check(o => o.Columns = []).Should().Throw<ValidationException>();
    [Fact] public void Too_many_columns_are_refused() => Check(o => o.Columns = Enumerable.Repeat("c", 1001).ToList()).Should().Throw<ValidationException>();
    [Fact] public void A_very_long_column_name_is_refused() => Check(o => o.Columns = [new string('x', 501)]).Should().Throw<ValidationException>();
    [Fact] public void The_layout_alone_can_be_checked() => ((Action)(() => ImportFileValidation.ValidateLayout(new ImportFileOptions { Format = "xlsx", HeaderRow = 2 }))).Should().NotThrow();

    [Theory]
    [InlineData("\\t", '\t')]
    [InlineData(";", ';')]
    [InlineData(null, null)]
    public void A_separator_becomes_its_character(string? text, char? expected)
        => new ImportFileOptions { Delimiter = text }.ToLayout().Delimiter.Should().Be(expected);
}

public class ImportFileUploadTests
{
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly IImportFileRepository _files = Substitute.For<IImportFileRepository>();
    private readonly IQueryContext _user = Substitute.For<IQueryContext>();
    private readonly UploadImportFileHandler _sut;

    public ImportFileUploadTests()
    {
        _user.UserId.Returns(5L);
        _storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(ci => new StoredFile { Name = ci.ArgAt<string>(1), Path = "/files/stored" + Path.GetExtension(ci.ArgAt<string>(1)), Size = 1 });
        _files.CreateAsync(Arg.Any<ImportFile>(), Arg.Any<CancellationToken>()).Returns(ci => { ci.Arg<ImportFile>().PublicId = Guid.NewGuid(); return 1L; });
        _sut = new UploadImportFileHandler(_access, _storage, _files, _user);
    }

    private Task<ImportFileInfo> Upload(byte[] bytes, string name, long? length = null) =>
        _sut.HandleAsync(Guid.NewGuid(), new MemoryStream(bytes), name, length ?? bytes.Length, default);

    private static byte[] Workbook(params string[] sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var s in sheets) wb.AddWorksheet(s).Cell("A1").Value = "x";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task A_csv_is_stored_and_recorded_for_the_uploader()
    {
        var info = await Upload(Encoding.UTF8.GetBytes("a,b\n1,2\n"), "data.csv");

        info.Format.Should().Be("csv");
        info.Sheets.Should().BeEmpty();
        await _files.Received(1).CreateAsync(Arg.Is<ImportFile>(f => f.UploadedByUserId == 5 && f.Format == "csv" && f.FileName == "data.csv"), Arg.Any<CancellationToken>());
        await _storage.Received(1).SaveAsync(Arg.Any<Stream>(), Arg.Is("data.csv"), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Is<string?>(k => k!.StartsWith("import-")));
    }

    [Fact]
    public async Task A_workbook_reports_its_sheets()
    {
        var info = await Upload(Workbook("Customers", "Orders"), "book.xlsx");

        info.Format.Should().Be("xlsx");
        info.Sheets.Should().Equal("Customers", "Orders");
    }

    [Fact]
    public async Task The_caller_must_be_allowed_to_add_records_to_the_table()
    {
        _access.RequirePermissionByTablePublicIdAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new UnauthorizedActionException("add records"));

        var act = () => Upload([1], "a.csv");

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await _storage.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task An_empty_file_is_refused() => await ((Func<Task>)(() => Upload([], "a.csv"))).Should().ThrowAsync<ValidationException>().WithMessage("*empty*");

    [Fact]
    public async Task A_file_over_the_limit_is_refused_before_it_is_stored()
    {
        var act = () => Upload([1], "a.csv", ImportFileFormats.MaxFileBytes + 1);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*larger than*");
        await _storage.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("report.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })]
    [InlineData("old.xls", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1 })]
    [InlineData("disguised.csv", new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14 })]
    public async Task A_file_that_is_not_csv_or_xlsx_is_refused_by_what_is_in_it(string name, byte[] bytes)
    {
        var act = () => Upload(bytes, name);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*cannot be imported*");
        await _storage.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_broken_workbook_is_refused_and_not_kept()
    {
        var act = () => Upload(Encoding.UTF8.GetBytes("PK\u0003\u0004 garbage garbage garbage"), "broken.xlsx");

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*.xlsx*");
        await _storage.Received(1).DeleteAsync("/files/stored.xlsx", Arg.Any<CancellationToken>());
        await _files.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task Too_many_waiting_uploads_stop_a_new_one()
    {
        _files.CountByUserAsync(5, Arg.Any<CancellationToken>()).Returns(UploadImportFileHandler.MaxOutstandingPerUser);

        var act = () => Upload([1, 2], "a.csv");

        await act.Should().ThrowAsync<ConflictException>();
        await _storage.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("..\\..\\windows\\evil.csv", "evil.csv")]
    [InlineData("/etc/passwd.csv", "passwd.csv")]
    [InlineData("a\u0000b.csv", "ab.csv")]
    [InlineData("   ", "upload")]
    public void The_shown_name_has_no_folders_or_control_characters(string given, string expected) => UploadImportFileHandler.SafeName(given).Should().Be(expected);
}

public class ImportFilePreviewAndCleanupTests
{
    private readonly IImportFileRepository _files = Substitute.For<IImportFileRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly IImportFileAccess _access = Substitute.For<IImportFileAccess>();
    private readonly IQueryContext _user = Substitute.For<IQueryContext>();

    public ImportFilePreviewAndCleanupTests() => _user.UserId.Returns(5L);

    private ImportFile Stored(string format, byte[] bytes, long owner = 5)
    {
        var file = new ImportFile { PublicId = Guid.NewGuid(), UploadedByUserId = owner, Format = format, StoragePath = "/files/x", FileName = "x." + format };
        _files.GetByPublicIdAsync(file.PublicId, Arg.Any<CancellationToken>()).Returns(file);
        _access.OpenAsync("/files/x", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream>(new MemoryStream(bytes)));
        return file;
    }

    [Fact]
    public async Task The_preview_shows_columns_rows_and_the_row_count()
    {
        var file = Stored("csv", Encoding.UTF8.GetBytes("Name;Qty\nPen;1\nPad;2\n"));

        var preview = await new PreviewImportFileHandler(_files, _access, _user).HandleAsync(file.PublicId, null, null, null, null, default);

        preview.Columns.Select(c => c.Name).Should().Equal("Name", "Qty");
        preview.DataRows.Should().Be(2);
        preview.Delimiter.Should().Be(";");
        preview.Sheets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_workbook_preview_reads_the_first_sheet_unless_one_is_chosen_and_lists_them_all()
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("One").Cell("A1").Value = "first-sheet";
        wb.AddWorksheet("Two").Cell("A1").Value = "second-sheet";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var file = Stored("xlsx", ms.ToArray());
        var sut = new PreviewImportFileHandler(_files, _access, _user);

        (await sut.HandleAsync(file.PublicId, null, null, null, null, default)).Columns.Single().Name.Should().Be("first-sheet");
        var second = await sut.HandleAsync(file.PublicId, "Two", null, null, null, default);

        second.Columns.Single().Name.Should().Be("second-sheet");
        second.Sheets.Should().Equal("One", "Two");
    }

    [Fact]
    public async Task A_file_of_someone_else_does_not_exist_for_you()
    {
        var file = Stored("csv", [1], owner: 99);

        await ((Func<Task>)(() => new PreviewImportFileHandler(_files, _access, _user).HandleAsync(file.PublicId, null, null, null, null, default)))
            .Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => new DiscardImportFileHandler(_files, _storage, _user).HandleAsync(file.PublicId, default)))
            .Should().ThrowAsync<NotFoundException>();
        await _storage.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    [Fact]
    public async Task A_sheet_that_is_not_there_is_reported_as_a_problem_with_the_file()
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("One").Cell("A1").Value = "x";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var file = Stored("xlsx", ms.ToArray());

        var act = () => new PreviewImportFileHandler(_files, _access, _user).HandleAsync(file.PublicId, "Nope", null, null, null, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*'Nope'*");
    }

    [Fact]
    public async Task Discarding_deletes_the_file_and_its_record()
    {
        var file = Stored("csv", [1]);

        await new DiscardImportFileHandler(_files, _storage, _user).HandleAsync(file.PublicId, default);

        await _storage.Received(1).DeleteAsync("/files/x", Arg.Any<CancellationToken>());
        await _files.Received(1).DeleteAsync(file.PublicId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cleanup_removes_uploads_older_than_a_day_and_keeps_a_record_it_could_not_delete_for_next_time()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0);
        var ok = new ImportFile { PublicId = Guid.NewGuid(), StoragePath = "/files/ok" };
        var stuck = new ImportFile { PublicId = Guid.NewGuid(), StoragePath = "/files/stuck" };
        _files.ListOlderThanAsync(now - TimeSpan.FromHours(24), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([ok, stuck]);
        _storage.DeleteAsync("/files/stuck", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new IOException("locked"));

        var count = await new ImportFileCleanup(_files, _storage).RunAsync(now, default);

        count.Should().Be(2);
        await _files.Received(1).DeleteAsync(ok.PublicId, Arg.Any<CancellationToken>());
        await _files.DidNotReceive().DeleteAsync(stuck.PublicId, Arg.Any<CancellationToken>());
    }
}

/// <summary>Saving and starting a file import, and the "needs attention" check for one.</summary>
public class ImportFileDefinitionTests
{
    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "From file", SourceKind = ImportSourceKinds.File,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Qty", "Note"] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    private static (SaveImportDefinitionHandler Handler, IImportDefinitionRepository Definitions) Saver()
    {
        var h = ImportHarness.Create(Config());
        var definitions = Substitute.For<IImportDefinitionRepository>();
        definitions.CreateAsync(Arg.Any<ImportDefinition>(), Arg.Any<CancellationToken>()).Returns(1L);
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(5L);
        return (new SaveImportDefinitionHandler(h.PlanBuilder, Substitute.For<IAppTableRepository>(), definitions, user), definitions);
    }

    [Fact]
    public async Task A_file_import_is_saved_without_a_source_table_and_keeps_its_file_settings()
    {
        var (sut, definitions) = Saver();

        await sut.HandleAsync(ImportHarness.DestTableId, null, Config(), default);

        await definitions.Received(1).CreateAsync(Arg.Is<ImportDefinition>(d =>
            d.SourceKind == "file" && d.SourceTableId == null && d.ScheduleJson == null && d.NextRunOn == null
            && d.OptionsJson!.Contains("\"columns\":[\"Name\",\"Qty\",\"Note\"]")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_file_settings_survive_a_round_trip_through_the_stored_row()
    {
        var def = new ImportDefinition();
        ImportConfigMapper.Apply(def, Config(c => { c.File!.Sheet = "Data"; c.File.Delimiter = ";"; c.NotifyEmails = ["a@b.co"]; }));

        var back = ImportConfigMapper.ToConfig(def, Guid.Empty);

        back.SourceKind.Should().Be("file");
        back.File!.Sheet.Should().Be("Data");
        back.File.Delimiter.Should().Be(";");
        back.File.Columns.Should().Equal("Name", "Qty", "Note");
        back.NotifyEmails.Should().Equal("a@b.co");
    }

    [Fact]
    public async Task A_file_import_cannot_be_scheduled()
    {
        var (sut, _) = Saver();
        var cfg = Config(c => c.Schedule = new ImportSchedule { Type = "daily", TimeOfDay = "09:00", TimeZone = "UTC" });

        await ((Func<Task>)(() => sut.HandleAsync(ImportHarness.DestTableId, null, cfg, default))).Should().ThrowAsync<ValidationException>().WithMessage("*schedule*");
    }

    [Fact]
    public async Task A_mapping_to_a_column_the_file_settings_do_not_list_is_refused_when_saving()
    {
        var (sut, _) = Saver();
        var cfg = Config(c => c.Mappings = [new() { DestFid = 6, SourceFid = 1009 }]);

        await ((Func<Task>)(() => sut.HandleAsync(ImportHarness.DestTableId, null, cfg, default))).Should().ThrowAsync<ValidationException>().WithMessage("*column*");
    }

    [Fact]
    public async Task A_file_import_with_no_file_settings_is_refused()
    {
        var (sut, _) = Saver();

        await ((Func<Task>)(() => sut.HandleAsync(ImportHarness.DestTableId, null, Config(c => c.File = null), default))).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task An_unknown_source_kind_is_refused()
    {
        var (sut, _) = Saver();

        await ((Func<Task>)(() => sut.HandleAsync(ImportHarness.DestTableId, null, Config(c => c.SourceKind = "ftp"), default))).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task A_table_import_does_not_keep_stray_file_settings()
    {
        var h = ImportHarness.Create();
        var definitions = Substitute.For<IImportDefinitionRepository>();
        definitions.CreateAsync(Arg.Any<ImportDefinition>(), Arg.Any<CancellationToken>()).Returns(1L);
        var user = Substitute.For<IQueryContext>();
        var sut = new SaveImportDefinitionHandler(h.PlanBuilder, Substitute.For<IAppTableRepository>(), definitions, user);
        var cfg = new ImportDefinitionConfig
        {
            Name = "T", SourceTableId = ImportHarness.SourceTableId, Mappings = [new() { DestFid = 6, SourceFid = 6 }],
            File = new ImportFileOptions { Columns = ["junk"] }
        };

        await sut.HandleAsync(ImportHarness.DestTableId, null, cfg, default);

        await definitions.Received(1).CreateAsync(Arg.Is<ImportDefinition>(d => d.SourceKind == "table" && (d.OptionsJson == null || !d.OptionsJson.Contains("junk"))), Arg.Any<CancellationToken>());
    }

    // ---- starting ----

    private sealed class Start
    {
        public IImportFileRepository Files { get; } = Substitute.For<IImportFileRepository>();
        public IImportFileAccess Access { get; } = Substitute.For<IImportFileAccess>();
        public IImportRunRepository Runs { get; } = Substitute.For<IImportRunRepository>();
        public StartImportRunHandler Handler { get; }
        public ImportDefinition Definition { get; }
        public ImportFile Upload { get; }

        public Start(string uploadFormat = "csv", long uploader = 5, bool tableImport = false)
        {
            var h = ImportHarness.Create(Config());
            var tables = Substitute.For<IAppTableRepository>();
            tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 1, PublicId = ImportHarness.DestTableId });
            tables.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10, AppId = 1, PublicId = ImportHarness.SourceTableId });
            Definition = new ImportDefinition { Id = 1, PublicId = Guid.NewGuid(), DestinationTableId = 11, ImportType = "copy" };
            ImportConfigMapper.Apply(Definition, tableImport
                ? new ImportDefinitionConfig { Name = "T", SourceTableId = ImportHarness.SourceTableId, Mappings = [new() { DestFid = 6, SourceFid = 6 }] }
                : Config());
            Definition.SourceTableId = tableImport ? 10 : null;
            var definitions = Substitute.For<IImportDefinitionRepository>();
            definitions.GetByPublicIdAsync(Definition.PublicId, Arg.Any<CancellationToken>()).Returns(Definition);
            Upload = new ImportFile { PublicId = Guid.NewGuid(), UploadedByUserId = uploader, Format = uploadFormat, StoragePath = "/files/x", FileName = "x.csv" };
            Files.GetByPublicIdAsync(Upload.PublicId, Arg.Any<CancellationToken>()).Returns(Upload);
            Access.OpenSourceAsync(Arg.Any<ImportRunSnapshot>(), false, Arg.Any<CancellationToken>()).Returns(ci =>
            {
                var options = ci.Arg<ImportRunSnapshot>().Config.File!;
                return new ImportFileSource(ImportFileColumns.FromHeader(options.Columns.ToArray(), options.Columns.Count), "x.csv", options.ToLayout(), "/files/x", 0, 0, options);
            });
            Runs.CreateAsync(Arg.Any<ImportRun>(), Arg.Any<CancellationToken>()).Returns(ci => { ci.Arg<ImportRun>().PublicId = Guid.NewGuid(); return 9L; });
            var user = Substitute.For<IQueryContext>();
            user.UserId.Returns(5L);
            user.TenantId.Returns(1L);
            Handler = new StartImportRunHandler(h.PlanBuilder, tables, Substitute.For<IAppRepository>(), definitions, Runs, Substitute.For<IImportQueue>(), user,
                Substitute.For<IImportDefinitionChecker>(), Files, Access);
        }

        public Task<ImportRunStart> Run(Guid? fileId) =>
            Handler.StartAsync(Definition.PublicId, ImportTrigger.Manual, null, null, null, null, fileId, default);
    }

    [Fact]
    public async Task A_file_run_remembers_the_file_and_never_sends_its_path_anywhere_but_the_snapshot()
    {
        var s = new Start();

        await s.Run(s.Upload.PublicId);

        await s.Runs.Received(1).CreateAsync(Arg.Is<ImportRun>(r =>
            ImportJson.Deserialize<ImportRunSnapshot>(r.DefinitionSnapshotJson)!.File!.FileId == s.Upload.PublicId
            && ImportJson.Deserialize<ImportRunSnapshot>(r.DefinitionSnapshotJson)!.File!.StoragePath == "/files/x"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_import_needs_a_file()
        => await ((Func<Task>)(() => new Start().Run(null))).Should().ThrowAsync<ValidationException>().WithMessage("*Choose the file*");

    [Fact]
    public async Task Someone_elses_upload_is_not_found()
    {
        var s = new Start(uploader: 99);

        await ((Func<Task>)(() => s.Run(s.Upload.PublicId))).Should().ThrowAsync<NotFoundException>();
        await s.Runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task An_unknown_upload_is_not_found()
        => await ((Func<Task>)(() => new Start().Run(Guid.NewGuid()))).Should().ThrowAsync<NotFoundException>();

    [Fact]
    public async Task A_file_of_the_wrong_type_is_refused_with_both_types_named()
    {
        var s = new Start(uploadFormat: "xlsx");

        await ((Func<Task>)(() => s.Run(s.Upload.PublicId))).Should().ThrowAsync<ValidationException>().WithMessage("*CSV*Excel*");
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_as_set_up_is_a_validation_problem_not_a_server_error()
    {
        var s = new Start();
        s.Access.OpenSourceAsync(Arg.Any<ImportRunSnapshot>(), false, Arg.Any<CancellationToken>())
            .Returns<Task<ImportFileSource>>(_ => throw new ImportFileFormatException("Row 1, which should hold the column names, is not in the file."));

        await ((Func<Task>)(() => s.Run(s.Upload.PublicId))).Should().ThrowAsync<ValidationException>().WithMessage("*column names*");
    }

    [Fact]
    public async Task A_file_missing_a_column_the_import_needs_is_refused_before_anything_is_queued()
    {
        var s = new Start();
        s.Access.OpenSourceAsync(Arg.Any<ImportRunSnapshot>(), false, Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var options = ci.Arg<ImportRunSnapshot>().Config.File!;
            return new ImportFileSource(ImportFileColumns.FromHeader(["Name", "Note"], 2), "x.csv", options.ToLayout(), "/files/x", 0, 0, options);
        });

        await ((Func<Task>)(() => s.Run(s.Upload.PublicId))).Should().ThrowAsync<ValidationException>().WithMessage("*'Qty'*");
        await s.Runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task A_table_import_given_a_file_is_refused()
    {
        var s = new Start(tableImport: true);

        await ((Func<Task>)(() => s.Run(s.Upload.PublicId))).Should().ThrowAsync<ValidationException>().WithMessage("*table*");
    }

    // ---- needs attention ----

    [Fact]
    public async Task A_file_import_is_checked_against_its_saved_columns_and_the_destination_only()
    {
        var tables = Substitute.For<IAppTableRepository>();
        var fields = Substitute.For<IAppFieldRepository>();
        var definitions = Substitute.For<IImportDefinitionRepository>();
        var dest = new List<AppField> { ImportHarness.RecordId(11), ImportHarness.Field(11, 6, "Name", "Text", unique: true), ImportHarness.Field(11, 7, "Qty", "Number") };
        tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11 });
        fields.ListByTableAsync(11, Arg.Any<CancellationToken>()).Returns(_ => dest);
        var checker = new ImportDefinitionChecker(tables, fields, new PowerBase.Formula.FormulaEngine(), definitions);
        var def = new ImportDefinition { Id = 1, DestinationTableId = 11, ImportType = "copy" };
        ImportConfigMapper.Apply(def, Config());
        def.SourceTableId = null;

        await checker.RefreshAsync(def, default);
        def.NeedsAttention.Should().BeFalse("no source table is needed, and text columns may go into number fields");
        await tables.DidNotReceive().GetByIdAsync(10, Arg.Any<CancellationToken>());

        dest.RemoveAll(f => f.Fid == 7);
        await checker.RefreshAsync(def, default);

        def.NeedsAttention.Should().BeTrue();
        def.AttentionReason.Should().Contain("deleted");
    }
}
