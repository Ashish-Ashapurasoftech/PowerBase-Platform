using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Imports;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.Imports.Tests;

/// <summary>An in-memory destination: records what the engine inserts and updates, and answers its lookups.</summary>
public sealed class FakeStore : IImportDataStore
{
    public int RowCount { get; init; }
    /// <summary>Values already in the destination (any field) and the Record ID# of the record holding each.</summary>
    public Dictionary<string, long> Existing { get; } = new(ImportKey.Comparer);
    /// <summary>What a column holds already, by Fid, for the values the engine streams to compare against.</summary>
    public Dictionary<int, List<object>> ColumnValues { get; } = new();
    public List<IReadOnlyDictionary<long, object?>> Inserted { get; } = new();
    public List<ImportUpdateRow> Updated { get; } = new();
    public int RecordCountAdded;
    /// <summary>What each destination table received (several tables may be filled in one run), by table id.</summary>
    public Dictionary<long, List<IReadOnlyDictionary<long, object?>>> InsertedByTable { get; } = new();
    public Dictionary<long, List<ImportUpdateRow>> UpdatedByTable { get; } = new();
    public Dictionary<long, int> RecordCountByTable { get; } = new();
    /// <summary>A write containing a row that matches is rejected, like a database constraint would.</summary>
    public Func<IReadOnlyDictionary<long, object?>, bool> Rejects { get; set; } = values => Equals(values.GetValueOrDefault(6), "BOOM");

    public Task<long> GetMaxRecordIdAsync(AppTable table, CancellationToken ct = default) => Task.FromResult<long>(RowCount);

    public async IAsyncEnumerable<object> StreamColumnValuesAsync(AppTable table, AppField field, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var value in ColumnValues.GetValueOrDefault(field.Fid!.Value) ?? []) yield return value;
        await Task.CompletedTask;
    }

    public Task<Dictionary<string, long>> FindRecordIdsAsync(AppTable table, AppField field, IReadOnlyCollection<object> values, CancellationToken ct = default)
    {
        var keys = values.Select(v => ImportKey.Normalize(v)!).ToList();
        // The Record ID# is its own identity for every record up to RowCount; other fields use the Existing map.
        var found = ImportTypeCompatibility.IsRecordId(field)
            ? keys.Where(k => long.TryParse(k, out var id) && id >= 1 && id <= RowCount).ToDictionary(k => k, long.Parse, ImportKey.Comparer)
            : keys.Where(Existing.ContainsKey).ToDictionary(k => k, k => Existing[k], ImportKey.Comparer);
        return Task.FromResult(found);
    }

    public Task InsertAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<IReadOnlyDictionary<long, object?>> rows, long createdBy, CancellationToken ct = default)
    {
        if (rows.Any(Rejects)) throw new ImportRowRejectedException("boom");
        Inserted.AddRange(rows);
        if (!InsertedByTable.TryGetValue(table.Id, out var list)) InsertedByTable[table.Id] = list = new();
        list.AddRange(rows);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<ImportUpdateRow> rows, long modifiedBy, CancellationToken ct = default)
    {
        if (rows.Any(r => Rejects(r.Values))) throw new ImportRowRejectedException("boom");
        Updated.AddRange(rows);
        if (!UpdatedByTable.TryGetValue(table.Id, out var list)) UpdatedByTable[table.Id] = list = new();
        list.AddRange(rows);
        return Task.CompletedTask;
    }

    public Task AddRecordCountAsync(long tableId, int count, CancellationToken ct = default)
    {
        RecordCountAdded += count;
        RecordCountByTable[tableId] = RecordCountByTable.GetValueOrDefault(tableId) + count;
        return Task.CompletedTask;
    }
}

/// <summary>An extra destination table of a multi-table import.</summary>
public sealed class HarnessDestination
{
    public long Id { get; init; }
    public Guid PublicId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Other";
    public List<AppField> Fields { get; init; } = new();
    public long AppId { get; init; } = 1;
}

public sealed class HarnessOptions
{
    public int RowCount { get; set; } = 2500; // more than one chunk (ImportSourceReader.ChunkSize), so a run needs two
    public Func<long, IReadOnlyDictionary<string, object?>>? Row { get; set; }
    public FilterGroup? Conditions { get; set; }
    public bool EncryptedSourceName { get; set; }
    public bool SameTable { get; set; }
    public bool LimitedDestination { get; set; }
    public bool DuplicateKeyValues { get; set; }
    public Action<List<AppField>>? ConfigureDestination { get; set; }
    public Action<List<AppField>>? ConfigureSource { get; set; }
    /// <summary>Makes the file storage throw, as a full disk or an unreachable blob service would.</summary>
    public bool FailFeedbackStorage { get; set; }
    /// <summary>For a file import: what the run reads its file through. Null leaves a stand-in that returns nothing.</summary>
    public PowerBase.Application.Imports.Files.IImportFileAccess? FileAccess { get; set; }
    /// <summary>For a file import: the bytes of the uploaded file. The run reads them through the real file access over a stand-in storage.</summary>
    public byte[]? UploadedFile { get; set; }
    /// <summary>More destination tables for an import that fills several: each has the given id, public id, name and fields.</summary>
    public List<HarnessDestination> ExtraDestinations { get; set; } = new();
    /// <summary>Simulates a user pressing Cancel: the run is told to stop when it records its Nth chunk.</summary>
    public int? CancelAtAdvance { get; set; }
    /// <summary>Simulates a run cancelled while it waited in the queue: it cannot be marked running.</summary>
    public bool CancelledWhileQueued { get; set; }
}

/// <summary>Everything an import run needs, wired to fakes, so a test only describes the data and the definition.</summary>
public sealed class ImportHarness
{
    public const int RowCount = 2500;
    public static readonly Guid SourceTableId = Guid.NewGuid();
    public static readonly Guid DestTableId = Guid.NewGuid();

    public ImportRunProcessor Processor { get; }
    public FakeStore Store { get; }
    public IImportRunRepository Runs { get; }
    public ImportRun Run { get; }
    public IImportNotifier Notifier { get; }
    /// <summary>The plan builder the processor uses, for tests of the handlers that validate with it.</summary>
    public ImportPlanBuilder PlanBuilder { get; private set; } = null!;
    /// <summary>Issues the run stored for its page (capped like the real table).</summary>
    public List<ImportRunIssue> Issues { get; } = new();
    /// <summary>A multi-table run's counts by table (0 = the import's own table), as the run's own record would add them up.</summary>
    public Dictionary<int, (long Inserted, long Updated, long Skipped, long Errored)> TargetCounters { get; } = new();
    /// <summary>Counters as the run's own record ends up with them (every chunk's counts added together).</summary>
    public (long Read, long Inserted, long Updated, long Skipped, long Errored) Counters { get; private set; }
    public (string Status, string? Detail, string? FeedbackPath)? Completion { get; private set; }
    /// <summary>The feedback file as it was uploaded, or null when none was.</summary>
    public string? FeedbackCsv { get; private set; }
    /// <summary>Every completion notification the run sent, with the run as it was at that moment.</summary>
    public List<(ImportRun Run, ImportRunSnapshot Snapshot)> Notifications { get; } = new();
    /// <summary>File imports: the storage the uploaded file was read from, and the record of the upload.</summary>
    public IFileStorageService? FileStorage { get; private set; }
    public IImportFileRepository? FileRepository { get; private set; }
    /// <summary>How many times the run recorded a chunk (each one is a chance for a user's cancel to be seen).</summary>
    public int Advances { get; private set; }

    private ImportHarness(ImportRunProcessor processor, FakeStore store, IImportRunRepository runs, ImportRun run, IImportNotifier notifier)
    {
        Processor = processor; Store = store; Runs = runs; Run = run; Notifier = notifier;
    }

    public static AppField RecordId(long tableId) => new()
    {
        Id = tableId * 100 + 3, AppTableId = tableId, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id"
    };

    public static AppField Field(long tableId, int fid, string name, string type, bool required = false, bool unique = false) => new()
    {
        Id = tableId * 100 + fid, AppTableId = tableId, Fid = fid, Name = name, Label = name, TypeCode = type, IsRequired = required, IsUnique = unique
    };

    /// <summary>Rows with a blank Name (5), a Name repeating row 6's (7), one that exists in the destination (9), one the write
    /// rejects (11) and one with a non-numeric Qty (13); every other row is clean.</summary>
    public static IReadOnlyDictionary<string, object?> DefaultRow(long id) => new Dictionary<string, object?>
    {
        ["Id"] = id,
        ["f_6"] = id switch { 5 => null, 7 => "n6", 9 => "EXIST", 11 => "BOOM", _ => $"n{id}" },
        ["f_8"] = id == 13 ? "abc" : id.ToString(CultureInfo.InvariantCulture),
        ["f_9"] = $"note{id}"
    };

    public static ImportHarness Create(ImportDefinitionConfig? definition = null, HarnessOptions? options = null)
    {
        options ??= new HarnessOptions();
        var rowCount = options.RowCount;
        var rowFactory = options.Row ?? DefaultRow;
        var source = new AppTable { Id = 10, AppId = 1, PublicId = SourceTableId, Name = "Src" };
        var dest = new AppTable { Id = 11, AppId = 1, PublicId = DestTableId, Name = "Dst" };

        var sourceName = Field(10, 6, "Name", "Text");
        sourceName.IsEncrypted = options.EncryptedSourceName;
        var sourceFields = new List<AppField> { RecordId(10), sourceName, Field(10, 8, "Qty", "Text"), Field(10, 9, "Note", "Text") };
        var destFields = new List<AppField>
        {
            RecordId(11), Field(11, 6, "Name", "Text", required: true, unique: true), Field(11, 7, "Qty", "Number"), Field(11, 9, "Note", "Text")
        };
        if (options.SameTable)
        {
            // One table acting as source and destination: Record ID#, Name, Qty (text), Note and Target (text) to copy into.
            sourceFields = [RecordId(10), Field(10, 6, "Name", "Text"), Field(10, 8, "Qty", "Text"), Field(10, 9, "Note", "Text"), Field(10, 7, "Target", "Text")];
            destFields = sourceFields;
        }
        options.ConfigureSource?.Invoke(sourceFields);
        options.ConfigureDestination?.Invoke(destFields);

        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByPublicIdAsync(SourceTableId, Arg.Any<CancellationToken>()).Returns(source);
        tables.GetByPublicIdAsync(DestTableId, Arg.Any<CancellationToken>()).Returns(options.SameTable ? source : dest);
        var fields = Substitute.For<IAppFieldRepository>();
        fields.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(sourceFields);
        fields.ListByTableAsync(11, Arg.Any<CancellationToken>()).Returns(destFields);
        foreach (var extra in options.ExtraDestinations)
        {
            var extraTable = new AppTable { Id = extra.Id, AppId = extra.AppId, PublicId = extra.PublicId, Name = extra.Name };
            tables.GetByPublicIdAsync(extra.PublicId, Arg.Any<CancellationToken>()).Returns(extraTable);
            tables.GetByIdAsync(extra.Id, Arg.Any<CancellationToken>()).Returns(extraTable);
            fields.ListByTableAsync(extra.Id, Arg.Any<CancellationToken>()).Returns(extra.Fields);
        }

        // A role that may edit the fields below but, like every role, has no edit right on the system Record ID# field.
        var enforcer = Substitute.For<IRolePermissionEnforcer>();
        var limitedRole = new TableAccessContext { Unrestricted = false, CanAdd = true, EditableFieldIds = new HashSet<long> { 6, 7, 9 }, VisibleFields = destFields };
        enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(call => options.LimitedDestination && call.ArgAt<AppTable>(0).Id == 11 ? limitedRole : new TableAccessContext { Unrestricted = true });

        // Source rows: Id 1..RowCount, honouring the reader's "Id > after AND Id <= max" keyset filter and page size.
        var records = Substitute.For<IRecordRepository>();
        records.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FilterGroup?>(),
                Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var bounds = call.ArgAt<FilterGroup>(4).Nodes.Where(n => n.Condition is { FieldId: 3 })
                    .ToDictionary(n => n.Condition!.Operator, n => long.Parse(n.Condition!.Value!, CultureInfo.InvariantCulture));
                var page = Enumerable.Range(1, rowCount).Select(i => (long)i).Where(i => i > bounds["gt"] && i <= bounds["lte"])
                    .Take(call.ArgAt<int>(3)).Select(rowFactory).ToList();
                return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)page;
            });
        records.HasDuplicatesAsync(Arg.Any<AppTable>(), Arg.Any<AppField>(), Arg.Any<CancellationToken>()).Returns(options.DuplicateKeyValues);

        var config = definition ?? new ImportDefinitionConfig
        {
            Name = "Test", SourceTableId = SourceTableId, ImportType = ImportTypes.Copy, Conditions = options.Conditions,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }]
        };
        IFileStorageService? fileStorage = null;
        IImportFileRepository? fileRepository = null;
        PowerBase.Application.Imports.Files.ImportRunFile? runFile = null;
        var fileAccess = options.FileAccess;
        if (options.UploadedFile is { } bytes)
        {
            fileStorage = Substitute.For<IFileStorageService, IFileStorageReadService>();
            ((IFileStorageReadService)fileStorage).OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<Stream>(new MemoryStream(bytes)));
            fileRepository = Substitute.For<IImportFileRepository>();
            fileAccess = new PowerBase.Application.Imports.Files.ImportFileAccess(fileStorage, fileRepository);
            runFile = new PowerBase.Application.Imports.Files.ImportRunFile(Guid.NewGuid(), "/files/upload.csv", "upload.csv");
        }
        var run = new ImportRun
        {
            Id = 1, PublicId = Guid.NewGuid(), Status = ImportRunStatus.Queued, TriggeredByUserId = 5,
            DefinitionSnapshotJson = ImportJson.Serialize(new ImportRunSnapshot(DestTableId, config, null, runFile))
        };

        var runs = Substitute.For<IImportRunRepository>();
        runs.GetByPublicIdAsync(run.PublicId, Arg.Any<CancellationToken>()).Returns(run);
        runs.MarkRunningAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(!options.CancelledWhileQueued);
        var store = new FakeStore { RowCount = rowCount };
        var storage = Substitute.For<IFileStorageService>();
        var notifier = Substitute.For<IImportNotifier>();
        var engine = new FormulaEngine();
        var planBuilder = new ImportPlanBuilder(tables, fields, Substitute.For<IAppAccessService>(), enforcer, Substitute.For<IUserRepository>(), Substitute.For<IQueryContext>(), records, engine);
        var processor = new ImportRunProcessor(
            runs,
            planBuilder,
            new ImportSourceReader(records, Substitute.For<IRelationalProjector>(), Substitute.For<IFormulaProjector>()),
            store, records, tables, fields, engine, Substitute.For<IAuditRepository>(), storage, notifier, fileAccess ?? Substitute.For<PowerBase.Application.Imports.Files.IImportFileAccess>(),
            new ImportMultiTargetRunner(runs, planBuilder, new ImportSourceReader(records, Substitute.For<IRelationalProjector>(), Substitute.For<IFormulaProjector>()),
                store, records, tables, fields, engine, Substitute.For<IAuditRepository>(), storage, fileAccess ?? Substitute.For<PowerBase.Application.Imports.Files.IImportFileAccess>(),
                NullLogger<ImportMultiTargetRunner>.Instance),
            NullLogger<ImportRunProcessor>.Instance);

        var harness = new ImportHarness(processor, store, runs, run, notifier) { PlanBuilder = planBuilder, FileStorage = fileStorage, FileRepository = fileRepository };
        harness.Capture(runs, storage, notifier, options);
        return harness;
    }

    /// <summary>Records what the run writes to its own log, the way the real repository would add it up.</summary>
    private void Capture(IImportRunRepository runs, IFileStorageService storage, IImportNotifier notifier, HarnessOptions options)
    {
        runs.AddIssuesAsync(Arg.Any<long>(), Arg.Do<IReadOnlyList<ImportRunIssue>>(Issues.AddRange), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        runs.AdvanceAsync(Arg.Any<long>(), Arg.Any<ImportChunkResult>(), Arg.Any<byte>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var c = call.ArgAt<ImportChunkResult>(1);
                Counters = (Counters.Read + c.RowsRead, Counters.Inserted + c.Inserted, Counters.Updated + c.Updated, Counters.Skipped + c.Skipped,
                    Counters.Errored + c.Errored);
                Advances++;
                return options.CancelAtAdvance is { } at && Advances >= at; // a user's cancel is reported by the same call that records the chunk
            });
        runs.AddTargetCountsAsync(Arg.Any<long>(), Arg.Do<IReadOnlyList<ImportTargetCounts>>(counts =>
            {
                foreach (var c in counts)
                {
                    var now = TargetCounters.GetValueOrDefault(c.Index);
                    TargetCounters[c.Index] = (now.Inserted + c.Inserted, now.Updated + c.Updated, now.Skipped + c.Skipped, now.Errored + c.Errored);
                }
            }), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        notifier.When(n => n.NotifyAsync(Arg.Any<ImportRun>(), Arg.Any<ImportRunSnapshot>(), Arg.Any<CancellationToken>()))
            .Do(call => Notifications.Add((call.ArgAt<ImportRun>(0), call.ArgAt<ImportRunSnapshot>(1))));
        runs.When(r => r.CompleteAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(call => Completion = (call.ArgAt<string>(1), call.ArgAt<string?>(2), call.ArgAt<string?>(3)));
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call => options.FailFeedbackStorage ? throw new IOException("disk full") : SaveFeedbackAsync(call.ArgAt<Stream>(0)));
    }

    private async Task<StoredFile> SaveFeedbackAsync(Stream content)
    {
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory);
        FeedbackCsv = Encoding.UTF8.GetString(memory.ToArray()).TrimStart('﻿');
        return new StoredFile { Path = "/files/feedback.csv", Size = memory.Length, ContentType = "text/csv" };
    }

    public Task RunAsync() => Processor.RunAsync(Run.PublicId, CancellationToken.None);

    /// <summary>Every source row must end up in exactly one outcome.</summary>
    public long Accounted => Counters.Inserted + Counters.Updated + Counters.Skipped + Counters.Errored;
}
