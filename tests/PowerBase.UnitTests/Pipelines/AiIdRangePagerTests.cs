using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Services;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// AiIdRangePager reads every id matching an Azure AI Search filter, 1,000 at a time, by splitting the id space into ranges
/// (no skip, no sort). The fake index below answers the pager's range filters exactly like the service would: it knows the
/// ids, applies "id ge 'x' and id lt 'y'" by ordinal string comparison, returns at most `size` of them and the total.
/// </summary>
public class AiIdRangePagerTests
{
    private const string Base = "tenantId eq '1' and tableId eq 5 and (X)";

    private sealed class FakeIndex
    {
        private readonly List<string> _ids;
        public List<string> Filters { get; } = new();
        public FakeIndex(IEnumerable<Guid> ids) => _ids = ids.Select(g => g.ToString()).OrderBy(i => i, StringComparer.Ordinal).ToList();

        private int LowerBound(string key)
        {
            var i = _ids.BinarySearch(key, StringComparer.Ordinal);
            return i >= 0 ? i : ~i;
        }

        public Task<(IReadOnlyList<Guid> Ids, long Total)> Query(string filter, int size, CancellationToken ct)
        {
            Filters.Add(filter);
            var lower = Regex.Match(filter, @"id ge '([^']*)'");
            var upper = Regex.Match(filter, @"id lt '([^']*)'");
            var from = lower.Success ? LowerBound(lower.Groups[1].Value) : 0;
            var to = upper.Success ? LowerBound(upper.Groups[1].Value) : _ids.Count;
            IReadOnlyList<Guid> page = _ids.Skip(from).Take(Math.Min(size, Math.Max(0, to - from))).Select(Guid.Parse).ToList();
            return Task.FromResult((page, (long)Math.Max(0, to - from)));
        }
    }

    private static async Task<List<AiSearchIdPage>> ReadAll(FakeIndex index, string? cursor = null, CancellationToken ct = default)
    {
        var pages = new List<AiSearchIdPage>();
        await foreach (var page in AiIdRangePager.PageAsync(Base, cursor, index.Query, ct)) pages.Add(page);
        return pages;
    }

    private static List<Guid> RandomIds(int count, int seed = 7)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => { var b = new byte[16]; rng.NextBytes(b); return new Guid(b); }).ToList();
    }

    [Fact]
    public async Task SmallResult_IsOneQueryAndOnePage()
    {
        var ids = RandomIds(40);
        var index = new FakeIndex(ids);

        var pages = await ReadAll(index);

        Assert.Single(index.Filters);
        Assert.Equal("(" + Base + ")", index.Filters[0]);          // no range: the whole id space
        Assert.Single(pages);
        Assert.Equal(ids.OrderBy(i => i.ToString(), StringComparer.Ordinal), pages[0].Ids.OrderBy(i => i.ToString(), StringComparer.Ordinal));
        Assert.Equal(AiIdRangePager.EndCursor, pages[0].NextCursor);
    }

    [Fact]
    public async Task NoMatches_YieldsNoPages()
    {
        var pages = await ReadAll(new FakeIndex([]));
        Assert.Empty(pages);
    }

    [Theory]
    [InlineData(1000)]    // exactly one page: no split
    [InlineData(1001)]    // one more than a page: split
    [InlineData(25000)]
    [InlineData(120000)]  // far past the old 50,000 cap
    public async Task EveryMatchIsReturnedExactlyOnce_NoCap_EachPageWithinTheServiceLimit(int count)
    {
        var ids = RandomIds(count);
        var index = new FakeIndex(ids);

        var pages = await ReadAll(index);

        var returned = pages.SelectMany(p => p.Ids).ToList();
        Assert.Equal(count, returned.Count);
        Assert.Equal(count, returned.Distinct().Count());
        Assert.Equal(ids.ToHashSet(), returned.ToHashSet());
        Assert.All(pages, p => Assert.InRange(p.Ids.Count, 1, AiIdRangePager.PageSize));
    }

    [Fact]
    public async Task PagesAreInIncreasingIdOrder_AndCursorsOnlyIncrease()
    {
        var index = new FakeIndex(RandomIds(30000));

        var pages = await ReadAll(index);

        var all = pages.SelectMany(p => p.Ids.Select(i => i.ToString())).ToList();
        Assert.Equal(all.OrderBy(i => i, StringComparer.Ordinal), all);
        var cursors = pages.Select(p => p.NextCursor).ToList();
        for (var i = 1; i < cursors.Count; i++)
            Assert.True(string.CompareOrdinal(cursors[i], cursors[i - 1]) > 0, $"cursor {cursors[i]} is not after {cursors[i - 1]}");
        Assert.Equal(AiIdRangePager.EndCursor, cursors[^1]);
    }

    [Fact]
    public async Task AFewThousandMatches_AreSplitOnceIntoJustEnoughPieces_NotSixteenWays()
    {
        var index = new FakeIndex(RandomIds(3000));

        var pages = await ReadAll(index);

        // 3,000 ids need about four pieces of ~800: the whole space, then four groups of digits. Sixteen single-digit pieces
        // would be sixteen queries of ~190 ids each.
        Assert.InRange(index.Filters.Count, 5, 7);
        Assert.Equal(3000, pages.SelectMany(p => p.Ids).Count());
        Assert.Contains(index.Filters, f => f.Contains("id ge '4'") && f.Contains("id lt '8'"));   // a group of digits, not one digit
    }

    [Fact]
    public async Task ManyMatches_KeepPagesFullAndQueriesNearTheMinimum()
    {
        const int count = 600_000;
        var index = new FakeIndex(RandomIds(count));

        var pages = await ReadAll(index);

        Assert.Equal(count, pages.Sum(p => p.Ids.Count));
        var minimum = count / AiIdRangePager.PageSize;                     // 600 pages if every one were full
        Assert.True(pages.Average(p => p.Ids.Count) >= 500, $"pages average only {pages.Average(p => p.Ids.Count):F0} ids");
        Assert.True(index.Filters.Count <= minimum * 3, $"{index.Filters.Count} queries for {count} ids (minimum {minimum})");
    }

    [Fact]
    public async Task QueryCount_ScalesWithMatches_NotWithTheNumberOfSplitCharacters()
    {
        // 16-way splitting makes 4,096 tiny pages of ~146 ids for 600,000 matches (and 70,000 for six million).
        var index = new FakeIndex(RandomIds(600_000));

        await ReadAll(index);

        Assert.True(index.Filters.Count < 2_500, $"{index.Filters.Count} queries");
    }

    [Fact]
    public async Task ResumingFromAnyPagesCursor_ReturnsExactlyTheRest()
    {
        var ids = RandomIds(20000);
        var full = await ReadAll(new FakeIndex(ids));

        // Interrupt after every possible page: reading on from its cursor must yield the remaining pages, no more, no less.
        for (var cut = 0; cut < full.Count; cut += Math.Max(1, full.Count / 12))
        {
            var rest = await ReadAll(new FakeIndex(ids), full[cut].NextCursor);
            var expected = full.Skip(cut + 1).SelectMany(p => p.Ids).ToList();
            Assert.Equal(expected, rest.SelectMany(p => p.Ids).ToList());
        }
    }

    [Fact]
    public async Task ResumeSkipsRangesAlreadyReadWithoutQueryingThem()
    {
        var ids = RandomIds(20000);
        var full = await ReadAll(new FakeIndex(ids));
        var lastCut = full[^3];                                   // resume close to the end
        var index = new FakeIndex(ids);

        await ReadAll(index, lastCut.NextCursor);

        var fullQueries = new FakeIndex(ids);
        await ReadAll(fullQueries);
        Assert.True(index.Filters.Count < fullQueries.Filters.Count / 4, $"resume made {index.Filters.Count} queries, a full read {fullQueries.Filters.Count}");
    }

    [Fact]
    public async Task EndCursor_MeansNothingLeftAndQueriesNothing()
    {
        var index = new FakeIndex(RandomIds(5000));

        var pages = await ReadAll(index, AiIdRangePager.EndCursor);

        Assert.Empty(pages);
        Assert.Empty(index.Filters);
    }

    [Fact]
    public async Task IdsSharingALongPrefix_AreStillSplitUntilEachRangeFitsOnePage()
    {
        // 3,000 ids that all start with the same 8 hex digits (a sequential-GUID-like clump), plus a few elsewhere.
        var clump = Enumerable.Range(0, 3000).Select(i => Guid.Parse($"a1b2c3d4-0000-0000-0000-{i:x12}")).ToList();
        var others = RandomIds(50);
        var index = new FakeIndex(clump.Concat(others));

        var pages = await ReadAll(index);

        Assert.Equal(3050, pages.SelectMany(p => p.Ids).Distinct().Count());
        Assert.All(pages, p => Assert.True(p.Ids.Count <= AiIdRangePager.PageSize));
    }

    [Fact]
    public async Task CountAndIdsDisagreeingWithinOnePage_SplitsInsteadOfTrustingAShortPage()
    {
        // A service whose count says 3 but returns 2 (a document not fully indexed yet) must not be taken as complete.
        var calls = 0;
        Task<(IReadOnlyList<Guid>, long)> Query(string filter, int size, CancellationToken ct)
        {
            calls++;
            IReadOnlyList<Guid> ids = filter.Contains("id ge") ? [Guid.NewGuid()] : [Guid.NewGuid(), Guid.NewGuid()];
            return Task.FromResult((ids, filter.Contains("id ge") ? 1L : 3L));
        }

        var pages = new List<AiSearchIdPage>();
        await foreach (var page in AiIdRangePager.PageAsync(Base, null, Query)) pages.Add(page);

        Assert.True(calls > 1);          // it split
        Assert.NotEmpty(pages);
    }

    [Fact]
    public async Task ARangeThatCannotBeSplitFurtherButStillHasTooManyIds_FailsLoudlyInsteadOfDroppingRecords()
    {
        // Every range reports 5,000 matches: even a single full-length id would "contain" more than a page.
        Task<(IReadOnlyList<Guid>, long)> Query(string filter, int size, CancellationToken ct) =>
            Task.FromResult(((IReadOnlyList<Guid>)Enumerable.Range(0, size).Select(_ => Guid.NewGuid()).ToList(), 5000L));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in AiIdRangePager.PageAsync(Base, null, Query)) { }
        });
    }

    [Fact]
    public async Task MissingTotalCount_IsTreatedAsMoreThanOnePageAndSplit()
    {
        var index = new FakeIndex(RandomIds(300));
        var asked = 0;
        Task<(IReadOnlyList<Guid>, long)> Query(string filter, int size, CancellationToken ct)
        {
            asked++;
            return index.Query(filter, size, ct).ContinueWith(t => (t.Result.Ids, -1L), ct);
        }

        var pages = new List<AiSearchIdPage>();
        // With no count it can never be sure a range is complete: it must keep splitting down to the end rather than guess.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var page in AiIdRangePager.PageAsync(Base, null, Query)) pages.Add(page);
        });
        Assert.True(asked > 1);
    }

    [Fact]
    public async Task Cancellation_StopsTheRead()
    {
        using var cts = new CancellationTokenSource();
        var index = new FakeIndex(RandomIds(50000));
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in AiIdRangePager.PageAsync(Base, null, index.Query, cts.Token))
            {
                if (++seen == 3) cts.Cancel();
            }
        });
        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task QueryFailure_PropagatesAfterThePagesAlreadyRead()
    {
        var index = new FakeIndex(RandomIds(20000));
        var calls = 0;
        Task<(IReadOnlyList<Guid>, long)> Query(string filter, int size, CancellationToken ct) =>
            ++calls > 6 ? throw new InvalidOperationException("Azure Search unavailable") : index.Query(filter, size, ct);

        var pages = new List<AiSearchIdPage>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var page in AiIdRangePager.PageAsync(Base, null, Query)) pages.Add(page);
        });

        // What it did read is valid to resume from.
        Assert.NotEmpty(pages);
        var resumed = await ReadAll(new FakeIndex(index.Filters.Count > 0 ? RandomIds(20000) : []), pages[^1].NextCursor);
        Assert.Equal(20000, pages.SelectMany(p => p.Ids).Count() + resumed.SelectMany(p => p.Ids).Count());
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("a", "b")]
    [InlineData("9", "a")]
    [InlineData("0", "1")]
    [InlineData("e", "f")]
    [InlineData("f", null)]
    [InlineData("af", "b")]
    [InlineData("ff", null)]
    [InlineData("a3", "a4")]
    [InlineData("a9", "aa")]
    [InlineData("12345678-", "12345678.")]
    [InlineData("12345678-f", "12345678.")]
    public void Successor_IsTheSmallestStringAboveEveryIdWithThatPrefix(string prefix, string? expected) =>
        Assert.Equal(expected, AiIdRangePager.Successor(prefix));

    [Fact]
    public void Successor_BoundsEveryIdWithThePrefixAndNoOthers()
    {
        foreach (var id in RandomIds(2000).Select(g => g.ToString()))
            foreach (var len in new[] { 1, 2, 3, 8, 9, 12 })
            {
                var prefix = id[..len];
                var upper = AiIdRangePager.Successor(prefix);
                Assert.True(string.CompareOrdinal(id, prefix) >= 0);
                if (upper != null) Assert.True(string.CompareOrdinal(id, upper) < 0, $"{id} must be below {upper}");
            }
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(7, 16)]
    [InlineData(8, 1)]     // the hyphen after the first group
    [InlineData(9, 16)]
    [InlineData(13, 1)]
    [InlineData(18, 1)]
    [InlineData(23, 1)]
    [InlineData(35, 16)]
    [InlineData(36, 0)]    // a whole id: nothing left to split
    public void NextCharacters_FollowTheGuidTextLayout(int prefixLength, int expectedCount) =>
        Assert.Equal(expectedCount, AiIdRangePager.NextCharacters(new string('a', prefixLength)).Count);

    [Fact]
    public void Compose_BuildsTheRangeFilter()
    {
        Assert.Equal("(B)", AiIdRangePager.Compose("B", "", null));
        Assert.Equal("(B) and id ge 'a3'", AiIdRangePager.Compose("B", "a3", null));
        Assert.Equal("(B) and id ge 'a3' and id lt 'a4'", AiIdRangePager.Compose("B", "a3", "a4"));
        Assert.Equal("(B) and id lt '1'", AiIdRangePager.Compose("B", "", "1"));
    }
}
