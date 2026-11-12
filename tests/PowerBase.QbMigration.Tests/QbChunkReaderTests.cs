using FluentAssertions;
using PowerBase.Application.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

public class QbChunkReaderTests
{
    /// <summary>Serves a fixed set of records in pages of the given sizes, like QuickBase's own size-based paging.</summary>
    private sealed class PagedClient(IReadOnlyList<QbRow> rows, int[] pageSizes, int? reportedTotal = null, int changeTotalOnPage = -1) : IQuickBaseClient
    {
        public int Calls { get; private set; }
        public List<int> Skips { get; } = [];
        public Task<IReadOnlyList<QbTable>> GetTablesAsync(string appId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QbField>> GetFieldsAsync(string tableId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<QbQueryPage> QueryAsync(string tableId, IReadOnlyList<int> select, string? where, int skip, int? top, CancellationToken ct = default)
        {
            Skips.Add(skip);
            var size = pageSizes[Math.Min(Calls, pageSizes.Length - 1)];
            var total = (reportedTotal ?? rows.Count) + (Calls == changeTotalOnPage ? 1 : 0);
            Calls++;
            var page = rows.Skip(skip).Take(size).ToList();
            return Task.FromResult(new QbQueryPage(page, total, page.Count, skip));
        }
    }

    private static List<QbRow> Rows(params long[] ids) => ids.Select(i => Row(i)).ToList();

    [Fact]
    public async Task Keeps_asking_with_a_growing_skip_until_it_has_every_record()
    {
        var client = new PagedClient(Rows(10, 11, 12, 13, 14, 15, 16), [3, 2, 5]);
        var rows = await new QbChunkReader(client).ReadAsync("t", [6], new QbRange(10, 20));
        rows.Select(r => r.RecordId).Should().Equal(10, 11, 12, 13, 14, 15, 16);
        client.Skips.Should().Equal(0, 3, 5);
    }

    [Fact]
    public async Task Fewer_records_than_quickbase_reported_is_an_error_not_a_partial_chunk()
    {
        var client = new PagedClient(Rows(10, 11, 12), [2, 0], reportedTotal: 5);
        var act = () => new QbChunkReader(client).ReadAsync("t", [6], new QbRange(10, 20));
        await act.Should().ThrowAsync<QbChunkShortException>();
    }

    [Fact]
    public async Task A_range_that_changes_while_being_read_is_an_error()
    {
        var client = new PagedClient(Rows(10, 11, 12, 13), [2], changeTotalOnPage: 1);
        var act = () => new QbChunkReader(client).ReadAsync("t", [6], new QbRange(10, 20));
        await act.Should().ThrowAsync<QbChunkChangedException>();
    }

    [Fact]
    public async Task A_record_outside_the_range_is_refused()
    {
        var client = new PagedClient(Rows(10, 25), [10]);
        var act = () => new QbChunkReader(client).ReadAsync("t", [6], new QbRange(10, 20));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside the range*");
    }

    [Fact]
    public async Task An_empty_range_reads_one_page_and_returns_nothing()
    {
        var client = new PagedClient([], [10]);
        (await new QbChunkReader(client).ReadAsync("t", [6], new QbRange(10, 20))).Should().BeEmpty();
        client.Calls.Should().Be(1);
    }
}
