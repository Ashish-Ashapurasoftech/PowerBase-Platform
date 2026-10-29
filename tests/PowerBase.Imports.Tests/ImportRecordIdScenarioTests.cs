using FluentAssertions;
using PowerBase.Application.Imports;

namespace PowerBase.Imports.Tests;

/// <summary>The Record ID# as a merge key beyond the basics (<see cref="ImportRecordIdMergeKeyTests"/>): the shapes a value can take, the
/// constraint policies, several tables in one run, a large run, and that nothing else is disturbed.</summary>
public class ImportRecordIdScenarioTests
{
    private static ImportDefinitionConfig ById(string policy = ImportConstraintPolicy.ImportValid) => new()
    {
        Name = "By id", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Merge, MergeKeyFid = 3, ConstraintPolicy = policy,
        Mappings = [new() { DestFid = 3, SourceFid = 8 }, new() { DestFid = 9, SourceFid = 9 }]
    };

    private static HarnessOptions WithIds(int rows, Func<long, string?> idOf) => new()
    {
        RowCount = rows,
        Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = idOf(id), ["f_9"] = $"note{id}" }
    };

    // ---- value shapes ----

    [Theory]
    [InlineData("5", true)]
    [InlineData(" 5 ", true)]
    [InlineData("005", true)]
    [InlineData("5.0", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData("abc", false)]
    [InlineData("5abc", false)]
    [InlineData("99999999999999999999999", false)]
    [InlineData("1e3", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public async Task One_value_either_updates_that_record_or_is_reported_and_never_inserted(string value, bool updates)
    {
        // Row 1 carries the value under test, aimed at record 5; every other row names a different record (10..18), so only row 1 can touch record 5.
        var h = ImportHarness.Create(ById(), WithIds(10, id => id == 1 ? value : (id + 8).ToString()));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Accounted.Should().Be(10);
        var row1 = h.Issues.Where(i => i.SourceRowRef == 1).ToList();
        if (updates)
        {
            h.Store.Updated.Select(u => u.RecordId).Should().Contain(5);
            row1.Should().BeEmpty();
        }
        else
        {
            h.Store.Updated.Select(u => u.RecordId).Should().NotContain(5);
            row1.Should().HaveCount(1);
            row1[0].Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    // ---- policies ----

    [Fact]
    public async Task Abort_if_any_issue_writes_nothing_when_one_record_id_is_not_found()
    {
        var h = ImportHarness.Create(ById(ImportConstraintPolicy.AbortIfAnyIssue), WithIds(20, id => id == 15 ? "999999" : id.ToString()));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Store.Updated.Should().BeEmpty("one bad row stops the whole import under this policy");
        h.Issues.Should().Contain(i => i.ReasonCode == ImportReason.RecordIdNotFound);
    }

    [Fact]
    public async Task Import_valid_rows_updates_the_good_ones_and_reports_each_bad_one_with_its_reason()
    {
        var h = ImportHarness.Create(ById(), WithIds(30, id => id % 10 == 0 ? "999999" : id % 7 == 0 ? "x" : id.ToString()));

        await h.RunAsync();

        var bad = Enumerable.Range(1, 30).Count(i => i % 10 == 0 || i % 7 == 0);
        h.Store.Updated.Should().HaveCount(30 - bad);
        h.Issues.Should().HaveCount(bad);
        h.Issues.Select(i => i.ReasonCode).Distinct().Should().BeSubsetOf([ImportReason.RecordIdNotFound, ImportReason.TypeMismatch, ImportReason.MergeKeyMissing]);
        h.Issues.Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.Message));
        h.Accounted.Should().Be(30);
        h.Counters.Read.Should().Be(30);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
    }

    // ---- the id is never written ----

    [Fact]
    public async Task The_record_id_is_never_among_the_values_written()
    {
        var h = ImportHarness.Create(ById(), WithIds(50, id => id.ToString()));

        await h.RunAsync();

        h.Store.Updated.Should().HaveCount(50).And.OnlyContain(u => !u.Values.ContainsKey(3));
    }

    // ---- large run (non-functional) ----

    [Fact]
    public async Task A_run_of_many_chunks_stays_exact_and_fast()
    {
        const int rows = 30_000;
        var h = ImportHarness.Create(ById(), WithIds(rows, id => id.ToString()));
        var started = DateTime.UtcNow;

        await h.RunAsync();

        h.Counters.Read.Should().Be(rows);
        h.Accounted.Should().Be(rows);
        h.Store.Inserted.Should().BeEmpty();
        h.Issues.Should().BeEmpty();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Ids_repeating_across_chunks_are_reported_once_per_repeat()
    {
        // 3000 rows over 2500 records: rows 2501..3000 repeat ids 1..500 (another chunk), each repeat is reported and the first use of an id wins.
        var h = ImportHarness.Create(ById(), WithIds(3000, id => ((id - 1) % ImportHarness.RowCount + 1).ToString()));

        await h.RunAsync();

        h.Store.Updated.Select(u => u.RecordId).Should().OnlyHaveUniqueItems();
        h.Store.Updated.Should().HaveCount(2500);
        h.Issues.Should().HaveCount(500);
        h.Accounted.Should().Be(3000);
    }

    // ---- several tables in one run ----

    [Fact]
    public async Task A_second_table_can_merge_on_its_own_record_id_while_the_home_table_copies()
    {
        const long contactsId = 12;
        var contactsPublicId = Guid.NewGuid();
        var contacts = new HarnessDestination
        {
            Id = contactsId, PublicId = contactsPublicId, Name = "Contacts",
            Fields = [ImportHarness.RecordId(contactsId), ImportHarness.Field(contactsId, 6, "Title", "Text")]
        };
        var cfg = new ImportDefinitionConfig
        {
            Name = "Two", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }],
            AdditionalTargets =
            [
                new ImportTargetConfig
                {
                    DestinationTableId = contactsPublicId, ImportType = ImportTypes.Merge, MergeKeyFid = 3,
                    Mappings = [new() { DestFid = 3, SourceFid = 8 }, new() { DestFid = 6, SourceFid = 9 }]
                }
            ]
        };
        var options = WithIds(20, id => id <= 15 ? id.ToString() : "999999");
        options.ExtraDestinations = [contacts];
        var h = ImportHarness.Create(cfg, options);

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(20, "the home table copies every row");
        h.Store.InsertedByTable.GetValueOrDefault(contactsId).Should().BeNullOrEmpty("a Record ID# merge never inserts");
        h.Store.UpdatedByTable[contactsId].Should().HaveCount(15);
        h.TargetCounters[1].Should().Be((0L, 15L, 0L, 5L));
        h.Issues.Should().Contain(i => i.ReasonCode == ImportReason.RecordIdNotFound && i.Message.StartsWith("Contacts:"));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
    }

    // ---- nothing else is disturbed ----

    [Fact]
    public async Task A_merge_on_another_key_is_unchanged_by_the_record_id_support()
    {
        var h = ImportHarness.Create(ImportRunProcessorTests.MergeOnName());

        await h.RunAsync();

        h.Store.Inserted.Should().NotBeEmpty();
        h.Issues.Should().NotContain(i => i.ReasonCode == ImportReason.RecordIdNotFound);
    }

    [Fact]
    public async Task A_copy_import_is_unchanged_and_never_matches_on_the_record_id()
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "Copy", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 9, SourceFid = 9 }]
        }, new HarnessOptions { RowCount = 40, Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = id.ToString(), ["f_9"] = $"note{id}" } });

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(40);
        h.Store.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task Mapping_the_record_id_while_another_field_is_the_key_is_refused()
    {
        var cfg = ById();
        cfg.MergeKeyFid = 6;
        cfg.Mappings = [new() { DestFid = 3, SourceFid = 8 }, new() { DestFid = 6, SourceFid = 6 }];
        var h = ImportHarness.Create(cfg, WithIds(5, id => id.ToString()));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Store.Updated.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }
}
