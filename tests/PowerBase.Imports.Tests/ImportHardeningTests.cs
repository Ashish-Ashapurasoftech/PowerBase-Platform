using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Querying;
using Xunit.Abstractions;

namespace PowerBase.Imports.Tests;

/// <summary>Hostile and oversized input: files built to hurt the server, and formulas that would reach beyond the row.</summary>
public class ImportSecurityTests
{
    /// <summary>A "workbook" whose one part is 60 MB of zeros: tiny on disk, huge when unpacked.</summary>
    private static MemoryStream ZipBomb(long bytes = 60L * 1024 * 1024)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var part = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open())
        {
            var block = new byte[1024 * 1024];
            for (long written = 0; written < bytes; written += block.Length) part.Write(block);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_workbook_that_expands_far_beyond_a_real_one_is_refused_before_anything_is_unpacked()
    {
        using var stream = ZipBomb();
        stream.Length.Should().BeLessThan(1024 * 1024, "the point of the attack is a small file");

        var act = () => ImportXlsxReader.Open(stream, null);

        act.Should().Throw<ImportFileFormatException>().WithMessage("*expands*");
    }

    [Fact]
    public void The_sheet_list_is_guarded_the_same_way()
    {
        using var stream = ZipBomb();

        var act = () => ImportXlsxReader.SheetNames(stream);

        act.Should().Throw<ImportFileFormatException>();
    }

    [Fact]
    public void A_real_workbook_is_not_mistaken_for_a_bomb()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Data");
        for (var r = 1; r <= 2000; r++) { ws.Cell(r, 1).Value = r; ws.Cell(r, 2).Value = "row " + r; }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var act = () => ImportXlsxReader.GuardArchive(ms);

        act.Should().NotThrow();
        ms.Position.Should().Be(0, "the guard leaves the stream where it found it");
    }

    [Fact]
    public async Task An_upload_that_is_a_bomb_is_refused_and_not_kept()
    {
        var storage = Substitute.For<IFileStorageService>();
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new StoredFile { Path = "/files/bomb.xlsx" });
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(5L);
        var sut = new UploadImportFileHandler(Substitute.For<IAppAccessService>(), storage, Substitute.For<IImportFileRepository>(), user);
        using var bomb = ZipBomb();

        var act = () => sut.HandleAsync(Guid.NewGuid(), bomb, "report.xlsx", bomb.Length, default);

        await act.Should().ThrowAsync<ValidationException>();
        await storage.Received(1).DeleteAsync("/files/bomb.xlsx", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_csv_made_of_one_endless_line_is_stopped_not_buffered()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', ImportFileFormats.MaxCellLength * 3)));
        using var reader = new ImportCsvReader(stream, ',');

        var act = () => reader.Read(out _, out _);

        act.Should().Throw<ImportFileFormatException>();
    }

    [Fact]
    public void A_csv_row_with_thousands_of_columns_is_stopped()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join(',', Enumerable.Repeat("a", ImportFileFormats.MaxColumns + 5))));
        using var reader = new ImportCsvReader(stream, ',');

        var act = () => reader.Read(out _, out _);

        act.Should().Throw<ImportFileFormatException>().WithMessage("*columns*");
    }

    [Fact]
    public void Binary_junk_named_csv_does_not_crash_the_reader()
    {
        var junk = new byte[50_000];
        new Random(1).NextBytes(junk);
        using var stream = new MemoryStream(junk);
        using var reader = new ImportCsvReader(stream, ',');

        var act = () => { while (reader.Read(out _, out _)) { } };

        // Either it reads (as odd text) or it refuses with the import's own message: never an unhandled error type.
        try { act(); } catch (ImportFileFormatException) { }
    }

    [Fact]
    public void Formulas_in_an_import_cannot_reach_other_tables_or_records()
    {
        // The row a formula runs against answers only for its own columns; every cross-table lookup finds nothing.
        IRecordContext context = new RowRecordContext(new Dictionary<string, object?> { ["f_1001"] = "x" }, new Dictionary<long, string> { [1001] = "f_1001" });

        context.GetValue(1001).Should().Be("x");
        context.QueryRecords("other-table", new RecordQuery([], [])).Should().BeEmpty();
        context.FindRecordByField("other-table", 6, "x").Should().BeNull();
        context.RecordExists("other-table", 1).Should().BeFalse();
        context.GetFieldValues("other-table", [1], 6).Should().BeEmpty();
        context.ResolveTableId("Customers").Should().BeEmpty();
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1)")]
    public void Text_a_spreadsheet_would_run_is_neutralised_in_the_details_file(string cell)
    {
        ImportFeedbackWriter.Cell(cell).TrimStart('"').Should().StartWith("'", "the details file is opened in Excel by the person who ran the import");
    }
}

/// <summary>Who may do what: each handler against the kinds of caller it must treat differently.</summary>
public class ImportPermissionMatrixTests
{
    private static IQueryContext User(long id, bool admin = false)
    {
        var u = Substitute.For<IQueryContext>();
        u.UserId.Returns(id);
        u.IsTenantAdmin.Returns(admin);
        u.TenantId.Returns(1L);
        return u;
    }

    [Fact]
    public async Task Deleting_an_import_needs_the_right_to_add_records_to_its_table()
    {
        var def = new ImportDefinition { Id = 1, PublicId = Guid.NewGuid(), DestinationTableId = 11 };
        var definitions = Substitute.For<IImportDefinitionRepository>();
        definitions.GetByPublicIdAsync(def.PublicId, Arg.Any<CancellationToken>()).Returns(def);
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, PublicId = Guid.NewGuid() });
        var access = Substitute.For<IAppAccessService>();
        access.RequirePermissionByTablePublicIdAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("add records"));

        var act = () => new DeleteImportDefinitionHandler(tables, access, definitions, User(9)).HandleAsync(def.PublicId, default);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
        await definitions.DidNotReceiveWithAnyArgs().DeleteAsync(default, default, default);
    }

    [Fact]
    public async Task Listing_and_opening_imports_need_membership_of_the_tables_app()
    {
        var table = new AppTable { Id = 11, PublicId = Guid.NewGuid() };
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByPublicIdAsync(table.PublicId, Arg.Any<CancellationToken>()).Returns(table);
        var access = Substitute.For<IAppAccessService>();
        access.RequireMembershipByTablePublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("see this app"));
        var definitions = Substitute.For<IImportDefinitionRepository>();

        var list = () => new ListImportDefinitionsHandler(tables, access, definitions, Substitute.For<IImportDefinitionChecker>()).HandleAsync(table.PublicId, default);

        await list.Should().ThrowAsync<UnauthorizedActionException>();
        await definitions.DidNotReceiveWithAnyArgs().ListByDestinationAsync(default, default);
    }

    [Theory]
    [InlineData(5, false, true)]   // the person who started the run
    [InlineData(6, false, false)]  // another member of the app
    [InlineData(6, true, true)]    // a tenant admin
    public void Only_the_starter_or_an_admin_sees_a_runs_rows_and_may_stop_it(long caller, bool admin, bool allowed)
        => ImportRunAccessProbe.CanSee(startedBy: 5, caller: User(caller, admin)).Should().Be(allowed);
}

/// <summary>The access rule lives in an internal helper; this reaches it through the public handler that applies it.</summary>
internal static class ImportRunAccessProbe
{
    public static bool CanSee(long startedBy, IQueryContext caller)
    {
        var run = new ImportRun
        {
            Id = 3, PublicId = Guid.NewGuid(), Status = "partial", FeedbackFileUrl = "/files/x", TriggeredByUserId = startedBy,
            DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(Guid.NewGuid(), new ImportDefinitionConfig { Name = "n" }))
        };
        var runs = Substitute.For<IImportRunRepository>();
        runs.GetByPublicIdAsync(run.PublicId, Arg.Any<CancellationToken>()).Returns(run);
        var detail = new GetImportRunHandler(Substitute.For<IAppAccessService>(), runs, caller).HandleAsync(run.PublicId, default).GetAwaiter().GetResult();
        return detail.HasFeedback;
    }
}

/// <summary>Volume: the readers and the engine on files far bigger than a test normally uses. The limits are deliberately loose; they
/// are there to catch an accidental O(n²) or a buffer that holds the whole file, not to benchmark.</summary>
[Trait("Category", "Load")]
public class ImportScaleTests(ITestOutputHelper output)
{
    private static long Heap() => GC.GetTotalMemory(false);

    [Fact]
    public void A_million_row_csv_streams_in_flat_memory()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16))
            {
                writer.WriteLine("id,name,qty,note");
                for (var i = 1; i <= 1_000_000; i++) writer.WriteLine($"{i},\"name, {i}\",{i % 97},some note text");
            }
            GC.Collect();
            var baseline = Heap();
            long peak = baseline, rows = 0;
            var watch = Stopwatch.StartNew();

            using (var stream = File.OpenRead(path))
            using (var reader = new ImportCsvReader(stream, ','))
                while (reader.Read(out _, out var cells))
                {
                    rows++;
                    if (rows % 100_000 == 0) peak = Math.Max(peak, Heap());
                }

            output.WriteLine($"1M-row CSV ({new FileInfo(path).Length / 1_048_576} MB): {watch.Elapsed.TotalSeconds:F1}s, heap grew {(peak - baseline) / 1_048_576} MB at most");
            rows.Should().Be(1_000_001);
            (peak - baseline).Should().BeLessThan(150L * 1_048_576, "rows are read one at a time and not kept");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(120));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_large_workbook_streams_without_loading_the_sheet()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            WriteWorkbook(path, 100_000);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var baseline = Heap();
            long peak = baseline, rows = 0;
            var watch = Stopwatch.StartNew();

            using (var stream = File.OpenRead(path))
            using (var reader = ImportXlsxReader.Open(stream, null))
                while (reader.Read(out _, out _))
                {
                    rows++;
                    if (rows % 10_000 == 0) peak = Math.Max(peak, Heap());
                }

            output.WriteLine($"100k-row workbook ({new FileInfo(path).Length / 1_048_576} MB): {watch.Elapsed.TotalSeconds:F1}s, heap grew {(peak - baseline) / 1_048_576} MB at most");
            rows.Should().Be(100_001);
            (peak - baseline).Should().BeLessThan(200L * 1_048_576);
        }
        finally { File.Delete(path); }
    }

    private static void WriteWorkbook(string path, int rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Data");
        ws.Cell(1, 1).Value = "id"; ws.Cell(1, 2).Value = "name"; ws.Cell(1, 3).Value = "when";
        for (var r = 2; r <= rows + 1; r++)
        {
            ws.Cell(r, 1).Value = r; ws.Cell(r, 2).Value = "name " + r; ws.Cell(r, 3).Value = new DateTime(2026, 1, 1).AddDays(r % 365);
        }
        wb.SaveAs(path);
    }

    private static string CsvWithDuplicates(int total, double duplicateShare)
    {
        var unique = (int)(total * (1 - duplicateShare));
        var sb = new StringBuilder(total * 20);
        sb.Append("Name,Qty,Note\n");
        for (var i = 1; i <= total; i++) sb.Append('n').Append(i <= unique ? i : i - unique).Append(',').Append(i % 9).Append(",x\n");
        return sb.ToString();
    }

    [Fact]
    public async Task Two_hundred_thousand_rows_with_thirty_percent_duplicates_are_all_accounted_for()
    {
        var h = ImportHarness.Create(FileConfig(), new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(CsvWithDuplicates(200_000, 0.30)) });
        var watch = Stopwatch.StartNew();

        await h.RunAsync();

        output.WriteLine($"200k-row file import: {watch.Elapsed.TotalSeconds:F1}s");
        h.Counters.Should().Be((200_000L, 140_000L, 0L, 0L, 60_000L));
        h.Accounted.Should().Be(200_000);
        // The run page keeps a capped list of issues (the repository enforces it); the details file has every row.
        await h.Runs.Received().AddIssuesAsync(Arg.Any<long>(), Arg.Any<IReadOnlyList<ImportRunIssue>>(), 5000, Arg.Any<CancellationToken>());
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
        h.Issues.Should().OnlyContain(i => i.ReasonCode == ImportReason.DuplicateInRun);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task With_remove_duplicates_the_same_file_skips_the_repeats_instead_of_failing_them()
    {
        var cfg = FileConfig();
        cfg.ColumnRules = [new ImportColumnRule { DestFid = 6, RemoveDuplicates = true }];
        var h = ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(CsvWithDuplicates(50_000, 0.30)) });

        await h.RunAsync();

        h.Counters.Should().Be((50_000L, 35_000L, 0L, 15_000L, 0L));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
    }

    [Fact]
    public async Task Abort_if_any_issue_writes_nothing_even_in_a_big_file()
    {
        var cfg = FileConfig();
        cfg.ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue;
        var h = ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(CsvWithDuplicates(50_000, 0.30)) });

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Store.Inserted.Should().BeEmpty();
    }

    private static ImportDefinitionConfig FileConfig() => new()
    {
        Name = "Big", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
        File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Qty", "Note"] },
        Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }, new() { DestFid = 9, SourceFid = 1003 }]
    };

    [SqlFact]
    public async Task The_database_writes_a_hundred_thousand_rows_in_chunks_and_updates_them_masked()
    {
        var db = new SqlFixture();
        await db.InitializeAsync();
        try
        {
            var watch = Stopwatch.StartNew();
            for (var start = 0; start < 100_000; start += ImportSourceReader.ChunkSize)
            {
                var chunk = Enumerable.Range(start, Math.Min(ImportSourceReader.ChunkSize, 100_000 - start))
                    .Select(i => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?> { [6] = "name " + i, [7] = (decimal)i, [9] = "note " + i }).ToList();
                await db.Store.InsertAsync(db.Table, db.Fields, chunk, createdBy: 5);
            }
            var inserted = watch.Elapsed;

            await using var c = new SqlConnection(SqlFixture.ConnectionString);
            var ids = (await c.QueryAsync<long>($"SELECT Id FROM data.t_{db.TableId} ORDER BY Id")).ToList();
            ids.Should().HaveCount(100_000);
            watch.Restart();
            for (var start = 0; start < 50_000; start += ImportSourceReader.ChunkSize)
            {
                var chunk = ids.Skip(start).Take(ImportSourceReader.ChunkSize).Select(id => new ImportUpdateRow(id, new Dictionary<long, object?> { [7] = 1m })).ToList();
                await db.Store.UpdateAsync(db.Table, db.Fields, chunk, modifiedBy: 7);
            }
            var updated = watch.Elapsed;

            output.WriteLine($"SQL: 100k inserted in {inserted.TotalSeconds:F1}s, 50k updated in {updated.TotalSeconds:F1}s");
            (await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM data.t_{db.TableId} WHERE f_7 = 1")).Should().Be(50_000);
            (await c.ExecuteScalarAsync<string>($"SELECT f_9 FROM data.t_{db.TableId} WHERE Id = {ids[10]}")).Should().Be("note 10", "a masked update leaves the other columns alone");
            inserted.Should().BeLessThan(TimeSpan.FromMinutes(2));
            updated.Should().BeLessThan(TimeSpan.FromMinutes(2));
        }
        finally { await db.DisposeAsync(); }
    }
}
