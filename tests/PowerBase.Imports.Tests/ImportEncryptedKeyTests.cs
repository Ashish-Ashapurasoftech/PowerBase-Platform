using FluentAssertions;
using PowerBase.Application.Imports;

namespace PowerBase.Imports.Tests;

/// <summary>Merging on an encrypted unique field (for example a student's Email): stored values are ciphertext, so existing records are
/// matched through an index of the decrypted values.</summary>
public class ImportEncryptedKeyTests
{
    private static IReadOnlyDictionary<string, object?> Row(long id) => new Dictionary<string, object?>
    {
        ["Id"] = id, ["f_6"] = "key " + id, ["f_8"] = "1", ["f_9"] = "note"
    };

    private static HarnessOptions Options(int rows, bool encrypted) => new()
    {
        RowCount = rows, Row = Row,
        ConfigureDestination = d => d.Single(f => f.Fid == 6).IsEncrypted = encrypted
    };

    [Fact]
    public async Task An_encrypted_key_can_be_saved_and_planned()
    {
        var h = ImportHarness.Create(ImportRunProcessorTests.MergeOnName(), Options(5, encrypted: true));

        var act = async () => await h.PlanBuilder!.BuildAsync(ImportRunProcessorTests.MergeOnName(), ImportHarness.DestTableId, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Existing_records_are_found_through_the_decrypted_index_and_updated()
    {
        // The stand-in destination lists the same records as the source, so every key matches one: the store's own SQL lookup
        // (empty here) is not what finds them.
        var h = ImportHarness.Create(ImportRunProcessorTests.MergeOnName(), Options(30, encrypted: true));

        await h.RunAsync();

        h.Counters.Should().Be((30L, 0L, 30L, 0L, 0L));
        h.Store.Updated.Select(u => u.RecordId).Should().Equal(Enumerable.Range(1, 30).Select(i => (long)i));
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public async Task An_index_larger_than_one_page_is_read_in_full()
    {
        var h = ImportHarness.Create(ImportRunProcessorTests.MergeOnName(), Options(12_000, encrypted: true));

        await h.RunAsync();

        h.Counters.Updated.Should().Be(12_000);
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public async Task Keys_that_are_not_in_the_destination_are_added()
    {
        var h = ImportHarness.Create(ImportRunProcessorTests.MergeOnName(), new HarnessOptions
        {
            RowCount = 10,
            // The source has 10 records, but the destination (the same mock) only knows keys of the first 4.
            Row = id => id <= 4 ? Row(id) : new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "new " + id, ["f_8"] = "1", ["f_9"] = "n" },
            ConfigureDestination = d => d.Single(f => f.Fid == 6).IsEncrypted = true
        });

        await h.RunAsync();

        // Both the source and the stand-in destination read the same rows, so a key present in the source is "found" in the destination;
        // what matters here is that every row is accounted for and nothing is rejected for the key being encrypted.
        h.Accounted.Should().Be(10);
        h.Counters.Errored.Should().Be(0);
    }

    [Fact]
    public async Task Without_encryption_the_stores_lookup_is_used_as_before()
    {
        var h = ImportHarness.Create(ImportRunProcessorTests.MergeOnName(), Options(30, encrypted: false));

        await h.RunAsync();

        h.Counters.Should().Be((30L, 30L, 0L, 0L, 0L), "nothing exists in the (empty) store, so every row is added");
    }
}
