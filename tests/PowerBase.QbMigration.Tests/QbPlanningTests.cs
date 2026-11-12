using FluentAssertions;
using PowerBase.Application.QbMigration;

namespace PowerBase.QbMigration.Tests;

public class QbChunkPlannerTests
{
    [Fact]
    public void Dense_ids_give_chunks_of_the_target_size_covering_every_id_once()
    {
        var ranges = QbChunkPlanner.Plan(1, 25_000, 25_000, 12_000);
        ranges.Should().Equal(new QbRange(1, 12_001), new QbRange(12_001, 24_001), new QbRange(24_001, 25_001));
    }

    [Fact]
    public void Sparse_ids_widen_the_ranges_so_chunks_still_hold_about_the_target()
    {
        // 1,000 records spread over ids 1..10,000: one record per 10 ids.
        var ranges = QbChunkPlanner.Plan(1, 10_000, 1_000, 100);
        ranges.Should().HaveCount(10);
        ranges[0].Should().Be(new QbRange(1, 1_001));
        ranges[^1].To.Should().Be(10_001);
    }

    [Fact]
    public void Ranges_are_contiguous_and_never_overlap()
    {
        var ranges = QbChunkPlanner.Plan(37, 99_991, 12_345, 777);
        ranges[0].From.Should().Be(37);
        ranges[^1].To.Should().Be(99_992);
        ranges.Zip(ranges.Skip(1)).Should().OnlyContain(p => p.First.To == p.Second.From);
    }

    [Fact]
    public void A_single_record_gives_one_range()
    {
        QbChunkPlanner.Plan(42, 42, 1, 12_000).Should().Equal(new QbRange(42, 43));
    }

    [Fact]
    public void An_empty_table_gives_no_ranges() => QbChunkPlanner.Plan(0, 0, 0, 100).Should().BeEmpty();

    [Fact]
    public void Halving_splits_at_the_middle_and_a_one_id_range_cannot_split()
    {
        QbChunkPlanner.Halve(new QbRange(100, 200)).Should().Be((new QbRange(100, 150), new QbRange(150, 200)));
        QbChunkPlanner.Halve(new QbRange(5, 6)).Should().BeNull();
    }

    [Fact]
    public void The_range_query_is_inclusive_from_and_exclusive_to() =>
        new QbRange(100, 200).Where.Should().Be("{3.GTE.100}AND{3.LT.200}");
}

public class QbTierPlannerTests
{
    [Fact]
    public void Parents_come_before_children_and_unrelated_tables_go_first()
    {
        var (tiers, cyclic) = QbTierPlanner.Plan([1, 2, 3, 4], [(2, 1), (3, 2), (3, 1)]);
        tiers.Should().BeEquivalentTo(new Dictionary<long, int> { [1] = 1, [4] = 1, [2] = 2, [3] = 3 });
        cyclic.Should().BeEmpty();
    }

    [Fact]
    public void Tables_in_a_cycle_are_returned_and_get_no_tier()
    {
        var (tiers, cyclic) = QbTierPlanner.Plan([1, 2, 3, 4], [(2, 3), (3, 2), (4, 2)]);
        cyclic.Should().Equal(2, 3, 4);
        tiers.Keys.Should().Equal(1);
    }

    [Fact]
    public void A_self_reference_does_not_count_as_a_cycle()
    {
        var (tiers, cyclic) = QbTierPlanner.Plan([1], [(1, 1)]);
        tiers[1].Should().Be(1);
        cyclic.Should().BeEmpty();
    }
}
