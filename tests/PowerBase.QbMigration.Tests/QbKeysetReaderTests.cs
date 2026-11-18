using FluentAssertions;
using PowerBase.Application.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

public class QbKeysetReaderTests
{
    /// <summary>Answers "{filter}AND{3.GT.n}" from a record set, in pages of the given size; records can change between pages.</summary>
    private sealed class KeysetClient(List<QbRow> rows, int pageSize) : IQuickBaseClient
    {
        public List<string?> Wheres { get; } = [];
        public Action<int>? BeforePage { get; init; }
        public Task<IReadOnlyList<QbTable>> GetTablesAsync(string appId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QbField>> GetFieldsAsync(string tableId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<QbQueryPage> QueryAsync(string tableId, IReadOnlyList<int> select, string? where, int skip, int? top, CancellationToken ct = default)
        {
            BeforePage?.Invoke(Wheres.Count);
            Wheres.Add(where);
            var after = long.Parse(where![(where.LastIndexOf("{3.GT.", StringComparison.Ordinal) + 6)..^1]);
            var matching = rows.Where(r => r.RecordId > after).OrderBy(r => r.RecordId).ToList();
            var page = matching.Take(pageSize).ToList();
            return Task.FromResult(new QbQueryPage(page, matching.Count, page.Count, 0));
        }
    }

    private static async Task<List<long>> ReadAll(IQuickBaseClient client, string? filter = null)
    {
        var ids = new List<long>();
        await foreach (var page in new QbKeysetReader(client).ReadAsync("t", [6], filter)) ids.AddRange(page.Select(r => r.RecordId));
        return ids;
    }

    [Fact]
    public async Task Pages_continue_after_the_last_record_id_received()
    {
        var client = new KeysetClient([Row(2), Row(5), Row(9), Row(10), Row(40)], pageSize: 2);
        (await ReadAll(client, "{2.OAF.'2026-11-15'}")).Should().Equal(2, 5, 9, 10, 40);
        client.Wheres.Should().Equal("{2.OAF.'2026-11-15'}AND{3.GT.0}", "{2.OAF.'2026-11-15'}AND{3.GT.5}", "{2.OAF.'2026-11-15'}AND{3.GT.10}");
    }

    [Fact]
    public async Task A_record_deleted_while_reading_does_not_shift_later_pages()
    {
        var rows = new List<QbRow> { Row(1), Row(2), Row(3), Row(4), Row(5), Row(6) };
        // Record 1 (already read) is deleted before the second page: skip-based paging would now miss record 3.
        var client = new KeysetClient(rows, pageSize: 2) { BeforePage = n => { if (n == 1) rows.RemoveAt(0); } };
        (await ReadAll(client)).Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public async Task An_empty_table_gives_no_pages()
    {
        (await ReadAll(new KeysetClient([], 10))).Should().BeEmpty();
    }

    [Fact]
    public async Task Record_ids_come_back_sorted()
    {
        var ids = await new QbKeysetReader(new KeysetClient([Row(7), Row(3), Row(5)], 1)).RecordIdsAsync("t");
        ids.Should().Equal(3, 5, 7);
    }

    [Fact]
    public void Missing_lists_powerbase_ids_quickbase_no_longer_has()
    {
        QbKeysetReader.Missing([1, 3, 4, 9], [1, 2, 3, 4, 5, 9, 12]).Should().Equal(2, 5, 12);
        QbKeysetReader.Missing([], [1, 2]).Should().Equal(1, 2);
        QbKeysetReader.Missing([1, 2], []).Should().BeEmpty();
    }

    [Fact]
    public void The_filter_starts_a_day_early_and_the_exact_check_trims_it()
    {
        var since = new DateTime(2026, 11, 16, 0, 30, 0, DateTimeKind.Utc);
        QbKeysetReader.ModifiedFilter(since).Should().Be("{2.OAF.'2026-11-15'}");
        QbKeysetReader.ModifiedSince(Row(1, (2, "\"2026-11-16T00:29:59Z\"")), since).Should().BeFalse();
        QbKeysetReader.ModifiedSince(Row(1, (2, "\"2026-11-16T00:30:00Z\"")), since).Should().BeTrue();
        QbKeysetReader.ModifiedSince(Row(1, (2, "\"2026-11-17T08:00:00.123Z\"")), since).Should().BeTrue();
        QbKeysetReader.ModifiedSince(Row(1), since).Should().BeTrue("a record without a readable date is copied again to be safe");
    }
}
