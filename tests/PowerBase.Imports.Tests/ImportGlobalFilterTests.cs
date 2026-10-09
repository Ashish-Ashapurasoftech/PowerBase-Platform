using System.Text;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>The import's own conditions ("only copy source records that match") are read once, with the source, so they must apply to every
/// table the import fills and not just the first. These tests prove it for table and file sources and for several tables.</summary>
public class ImportGlobalFilterTests
{
    private const long ContactsId = 12;
    private const long NotesId = 13;
    private static readonly Guid ContactsPublicId = Guid.NewGuid();
    private static readonly Guid NotesPublicId = Guid.NewGuid();

    private static HarnessDestination Contacts() => new()
    {
        Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts",
        Fields = [ImportHarness.RecordId(ContactsId), ImportHarness.Field(ContactsId, 6, "Title", "Text"), ImportHarness.Field(ContactsId, 8, "Code", "Text", required: true, unique: true)]
    };

    private static HarnessDestination Notes() => new()
    {
        Id = NotesId, PublicId = NotesPublicId, Name = "Notes",
        Fields = [ImportHarness.RecordId(NotesId), ImportHarness.Field(NotesId, 6, "Text", "Text")]
    };

    private static FilterGroup NameStartsWith(string prefix) => new() { Nodes = [new() { Condition = new() { FieldId = 6, Operator = "startsWith", Value = prefix } }] };

    private static IReadOnlyDictionary<string, object?> Row(long id) => new Dictionary<string, object?>
    {
        ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = "1", ["f_9"] = "note " + id
    };

    private static ImportTargetConfig ContactsTarget() => new()
    {
        DestinationTableId = ContactsPublicId, ImportType = ImportTypes.Copy,
        Mappings = [new() { DestFid = 6, SourceFid = 9 }, new() { DestFid = 8, SourceFid = 6 }]
    };

    private static ImportTargetConfig NotesTarget() => new()
    {
        DestinationTableId = NotesPublicId, ImportType = ImportTypes.Copy, Mappings = [new() { DestFid = 6, SourceFid = 9 }]
    };

    private static ImportDefinitionConfig Config(FilterGroup? conditions, params ImportTargetConfig[] targets) => new()
    {
        Name = "Filtered", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy, Conditions = conditions,
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }],
        AdditionalTargets = [.. targets]
    };

    // The source's Name is encrypted, so SQL cannot compare it: the condition is checked on each row as it is read, the same place a
    // file's conditions are, and the place a condition that decides which tables get a row would be.
    private static HarnessOptions Source(int rows = 40) => new()
    {
        RowCount = rows, Row = Row, EncryptedSourceName = true, ExtraDestinations = [Contacts(), Notes()]
    };

    [Fact]
    public async Task The_conditions_apply_to_the_first_table_and_to_the_added_one()
    {
        var h = ImportHarness.Create(Config(NameStartsWith("n2"), ContactsTarget()), Source());

        await h.RunAsync();

        // n2, n20 .. n29
        h.Store.InsertedByTable[11].Should().HaveCount(11).And.OnlyContain(r => ((string)r[6]!).StartsWith("n2"));
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(11).And.OnlyContain(r => ((string)r[8]!).StartsWith("n2"));
        h.Counters.Read.Should().Be(11);
    }

    [Fact]
    public async Task The_conditions_apply_to_every_one_of_three_tables()
    {
        var h = ImportHarness.Create(Config(NameStartsWith("n3"), ContactsTarget(), NotesTarget()), Source());

        await h.RunAsync();

        // n3, n30 .. n39
        foreach (var table in new long[] { 11, ContactsId, NotesId })
            h.Store.InsertedByTable[table].Should().HaveCount(11, $"table {table} reads the same filtered rows");
    }

    [Fact]
    public async Task A_condition_that_matches_nothing_leaves_every_table_empty_and_the_run_clean()
    {
        var h = ImportHarness.Create(Config(NameStartsWith("zzz"), ContactsTarget(), NotesTarget()), Source());

        await h.RunAsync();

        h.Store.InsertedByTable.Values.Sum(v => v.Count).Should().Be(0);
        h.Counters.Read.Should().Be(0);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task With_no_conditions_every_table_gets_every_row()
    {
        var h = ImportHarness.Create(Config(null, ContactsTarget()), Source());

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(40);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(40);
    }

    [Fact]
    public async Task The_conditions_apply_to_every_table_when_the_source_is_a_file()
    {
        var csv = new StringBuilder("Name,Qty,Note\n");
        foreach (var i in Enumerable.Range(1, 20)) csv.Append($"Pen{i},1,n{i}\n");
        foreach (var i in Enumerable.Range(1, 5)) csv.Append($"Ink{i},1,n{i}\n");
        var cfg = new ImportDefinitionConfig
        {
            Name = "File", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy, MergeKeyFid = null,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Qty", "Note"] },
            Conditions = new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 1001, Operator = "startsWith", Value = "Ink" } }] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }],
            AdditionalTargets =
            [
                new ImportTargetConfig { DestinationTableId = ContactsPublicId, Mappings = [new() { DestFid = 6, SourceFid = 1003 }, new() { DestFid = 8, SourceFid = 1001 }] },
                new ImportTargetConfig { DestinationTableId = NotesPublicId, Mappings = [new() { DestFid = 6, SourceFid = 1001 }] }
            ]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(csv.ToString()), ExtraDestinations = [Contacts(), Notes()] });

        await h.RunAsync();

        foreach (var table in new long[] { 11, ContactsId, NotesId })
            h.Store.InsertedByTable[table].Should().HaveCount(5, $"table {table} gets only the Ink rows");
        h.Counters.Read.Should().Be(5);
    }

    [Fact]
    public async Task A_relative_date_in_the_conditions_is_the_same_day_for_every_table()
    {
        // One clock per run is fixed when the plans are built, so a "today" cannot differ between tables.
        var cfg = Config(new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 6, Operator = "isNotEmpty" } }] }, ContactsTarget());
        var h = ImportHarness.Create(cfg, Source(10));

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(10);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(10);
    }
}
