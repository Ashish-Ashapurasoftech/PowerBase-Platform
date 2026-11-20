using FluentAssertions;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

/// <summary>CFTSS Client Weeks points at itself by key ("PreviousWeekRef" holds the key of another Client Weeks record). The
/// parent can sit in a chunk that isn't loaded yet, so the keys are read from QuickBase instead of PowerBase.</summary>
public class QbKeyIndexTests
{
    private sealed class OneTableClient(params QbRow[] rows) : IQuickBaseClient
    {
        public int Queries { get; private set; }
        public bool FailOnce { get; set; }
        public Task<IReadOnlyList<QbTable>> GetTablesAsync(string appId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QbField>> GetFieldsAsync(string tableId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QbQueryPage> QueryAsync(string tableId, IReadOnlyList<int> select, string? where, int skip, int? top, CancellationToken ct = default)
        {
            Queries++;
            if (FailOnce) { FailOnce = false; throw new InvalidOperationException("QuickBase is down"); }
            var after = long.Parse(where![(where.LastIndexOf("{3.GT.", StringComparison.Ordinal) + 6)..^1]);
            var page = rows.Where(r => r.RecordId > after).OrderBy(r => r.RecordId).ToList();
            return Task.FromResult(new QbQueryPage(page, page.Count, page.Count, 0));
        }
    }

    private static QbRow Week(long id, string? key) => key is null ? Row(id) : Row(id, (6, $"\"{key}\""));

    [Fact]
    public async Task Keys_map_to_the_record_ids_of_the_whole_table()
    {
        var map = await QbKeyIndex.BuildAsync(new OneTableClient(Week(10, "W1"), Week(11, "W2"), Week(12, null), Week(13, "")), new("bwk", 6));
        map.Should().BeEquivalentTo(new Dictionary<string, long> { ["W1"] = 10, ["W2"] = 11 });
    }

    [Fact]
    public async Task A_duplicated_key_goes_to_the_highest_record_id_like_the_powerbase_side_lookup()
    {
        var map = await QbKeyIndex.BuildAsync(new OneTableClient(Week(10, "W1"), Week(11, "W1")), new("bwk", 6));
        map["W1"].Should().Be(11);
    }

    [Fact]
    public async Task A_number_key_is_read_as_its_raw_text_like_a_childs_key_value()
    {
        var map = await QbKeyIndex.BuildAsync(new OneTableClient(Row(10, (6, "17"))), new("bwk", 6));
        map.Should().ContainKey("17");
    }

    [Fact]
    public async Task The_table_is_read_once_for_every_chunk_of_the_process()
    {
        var client = new OneTableClient(Week(10, "W1"));
        var indexes = new QbKeyIndexes(client);
        await indexes.GetAsync(new("bwk", 6));
        var queries = client.Queries;
        await indexes.GetAsync(new("bwk", 6));
        client.Queries.Should().Be(queries);
    }

    [Fact]
    public async Task A_failed_read_is_not_kept_and_the_next_chunk_reads_again()
    {
        var client = new OneTableClient(Week(10, "W1")) { FailOnce = true };
        var indexes = new QbKeyIndexes(client);
        var act = () => indexes.GetAsync(new("bwk", 6));
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await indexes.GetAsync(new("bwk", 6))).Should().ContainKey("W1");
    }

    private static QbmTableContext SelfReferencingContext(out int referenceFid)
    {
        referenceFid = 7;
        var keyField = Pb(6, "Text", "Week ID");
        List<QbField> qbFields = [.. QbSystemFields(), new QbField(6, "Week ID", "text", null, []), new QbField(7, "PreviousWeekRef", "text", null, [])];
        List<AppField> pbFields = [.. SystemFields(), keyField, Pb(7, "Reference", "PreviousWeekRef")];
        var maps = QbFieldMapper.Map(qbFields, pbFields);
        var self = new QbmPowerBaseReader.PbReference(500, 7, 500, keyField);
        return new QbmTableContext(1, 1, 1, "bwk", new AppTable { Id = 500, AppId = 5, Name = "Client Weeks" }, pbFields, maps,
            new Dictionary<int, QbmPowerBaseReader.PbReference> { [7] = self }, new QbTransformOptions { FallbackUserId = 99 });
    }

    [Fact]
    public async Task A_record_whose_previous_week_is_in_a_later_chunk_still_links_to_it()
    {
        // Chunk 1 holds weeks 10 and 11; week 10's "previous week" is W9, record 9... and week 11's is W20, which only a later chunk holds.
        var client = new OneTableClient(Week(9, "W9"), Row(10, (6, "\"W10\""), (7, "\"W9\"")), Row(11, (6, "\"W11\""), (7, "\"W20\"")), Week(20, "W20"));
        var ctx = SelfReferencingContext(out _);
        var loader = new QbmLookupLoader(new QbmPowerBaseReader(null!, () => null!), selfKeys: new QbKeyIndexes(client));
        var lookups = await loader.LoadAsync(ctx, [Row(10, (6, "\"W10\""), (7, "\"W9\"")), Row(11, (6, "\"W11\""), (7, "\"W20\""))], default);

        var transformer = new QbValueTransformer(ctx.Mappings, lookups, ctx.Options, ctx.ReferencesByKey.Keys.ToList());
        var issues = new List<QbIssue>();
        transformer.Transform(Row(10, (6, "\"W10\""), (7, "\"W9\"")), issues).Values[7].Should().Be(9L);
        transformer.Transform(Row(11, (6, "\"W11\""), (7, "\"W20\"")), issues).Values[7].Should().Be(20L);
        transformer.Transform(Row(12, (6, "\"W12\""), (7, "\"nobody\"")), issues).Values[7].Should().BeNull();
        issues.Should().ContainSingle(i => i.Code == "QBM_REFERENCE_NOT_FOUND" && i.QbFid == 7);
    }

    [Fact]
    public async Task A_self_link_whose_key_field_is_not_loaded_stops_instead_of_matching_nothing()
    {
        var ctx = SelfReferencingContext(out _);
        var noKey = ctx with { Mappings = ctx.Mappings.Where(m => m.PbFid != 6).ToList() };
        var loader = new QbmLookupLoader(new QbmPowerBaseReader(null!, () => null!), selfKeys: new QbKeyIndexes(new OneTableClient()));
        var act = () => loader.LoadAsync(noKey, [Row(10, (7, "\"W9\""))], default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a loaded field*");
    }
}
