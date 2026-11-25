using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PowerBase.Application.Compute;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Compute;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>The build-speed pieces that need no database: Id ranges of a split build, deadlock detection, and the new options.</summary>
public class BuildSpeedTests
{
    [Theory]
    [InlineData(1, 1_000_000, 16)]
    [InlineData(500, 1_140_000, 8)]
    [InlineData(1, 100, 4)]
    public void SliceRanges_CoverEveryId_WithoutGapsOrOverlap(long minId, long maxId, int count)
    {
        var ranges = MaterializeBuilder.SliceRanges(minId, maxId, count)!;

        ranges.Should().NotBeNull();
        ranges[0].After.Should().Be(0, "the first range starts below the smallest Id");
        ranges[^1].UpTo.Should().Be(long.MaxValue, "rows added after the bounds were read are still covered");
        for (var i = 1; i < ranges.Count; i++)
            ranges[i].After.Should().Be(ranges[i - 1].UpTo, "each range starts where the previous one ends");
        // every Id of the table falls into exactly one range (After < Id <= UpTo)
        foreach (var id in new[] { minId, (minId + maxId) / 2, maxId })
            ranges.Count(r => id > r.After && id <= r.UpTo).Should().Be(1);
    }

    [Fact]
    public void SliceRanges_AreRoughlyEqualWidth()
    {
        var ranges = MaterializeBuilder.SliceRanges(1, 1_600_000, 16)!;
        var widths = ranges.Take(ranges.Count - 1).Select(r => r.UpTo - r.After).ToList();
        widths.Max().Should().BeLessThanOrEqualTo(widths.Min() + 1);
    }

    [Theory]
    [InlineData(1, 1000, 1)]       // one range is no split
    [InlineData(5, 5, 4)]          // a single Id cannot be split
    public void SliceRanges_ReturnsNull_WhenNothingToSplit(long minId, long maxId, int count) =>
        MaterializeBuilder.SliceRanges(minId, maxId, count).Should().BeNull();

    private static SqlException SqlError(int number)
    {
        // SqlException has no public constructor; build it the way the SqlClient tests do.
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var error = (SqlError)Activator.CreateInstance(typeof(SqlError), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null,
            new object?[] { number, (byte)0, (byte)0, "server", "deadlock", "proc", 0, (Exception?)null, -1 }, null)!;
        typeof(SqlErrorCollection).GetMethod("Add", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(collection, new object[] { error });
        return (SqlException)typeof(SqlException).GetMethod("CreateException", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(SqlErrorCollection), typeof(string) }, null)!.Invoke(null, new object[] { collection, "11.0.0" })!;
    }

    [Fact]
    public void IsDeadlock_RecognisesError1205_AlsoWhenWrapped()
    {
        var deadlock = SqlError(1205);
        MaterializedColumnWriter.IsDeadlock(deadlock).Should().BeTrue();
        MaterializedColumnWriter.IsDeadlock(new InvalidOperationException("wrapped", deadlock)).Should().BeTrue();
        MaterializedColumnWriter.IsDeadlock(SqlError(2627)).Should().BeFalse();
        MaterializedColumnWriter.IsDeadlock(new InvalidOperationException("other")).Should().BeFalse();
    }

    // Dept (20) <- Emp (21, f_6 -> Dept)
    private static AppField F(long table, int fid, string type, string? settings = null) =>
        new() { Id = table * 1000 + fid, AppTableId = table, Fid = fid, TypeCode = type, Name = $"F{table}_{fid}", Settings = settings };

    [Fact]
    public void OwnTableReads_ListOnlyTheColumnsAFieldIsWorkedOutFrom()
    {
        // Emp: 6 = Department (reference), 7 = Salary, 8..9 = unrelated columns; 15 = Lookup of Dept's name; 16 = Summary (on Dept) over Emp.Salary
        var lookup = F(21, 15, "Lookup", System.Text.Json.JsonSerializer.Serialize(
            new { SourceTableId = 20L, ReferenceFid = 6, SourceFid = 6, SourceTypeCode = "Text", ResultTypeCode = "Text" }));
        var summary = F(20, 8, "Summary", System.Text.Json.JsonSerializer.Serialize(
            new { ChildTableId = 21L, ReferenceFid = 6, Function = "Sum", TargetFid = 7, TargetTypeCode = "Number", ResultTypeCode = "Number" }));
        var schema = new ComputeSchema(
            new List<AppField>
            {
                F(20, 6, "Text"), F(21, 6, "Reference"), F(21, 7, "Number"), F(21, 8, "Text"), F(21, 9, "Text"), lookup, summary
            },
            new Dictionary<long, long> { [20] = 1, [21] = 1 }, new HashSet<long>());
        var model = new ComputeModel(schema);

        // The lookup (on Emp) needs Emp's reference column only: nothing of Emp's other fields.
        var lookupReads = model.OwnTableReadsOf(model.Decisions[new FieldKey(21, 15)].Decision.Plan!);
        lookupReads.Should().BeEquivalentTo(new[] { 6 });

        // The summary (on Dept) reads Dept rows only through their ids: no Dept column at all (its child's columns are read by the read code itself).
        var summaryReads = model.OwnTableReadsOf(model.Decisions[new FieldKey(20, 8)].Decision.Plan!);
        summaryReads.Should().BeEmpty();
    }

    [Fact]
    public void Options_Defaults_NightlyCheckWaitsForTheBacklog()
    {
        var o = new ComputeOptions();
        o.ReconcileWhileBuilding.Should().BeFalse();
        o.ReconcileMinAgeHours.Should().Be(20);
        o.NarrowReads.Should().BeTrue();
    }

    [Fact]
    public void Config_ReadsTheNewReconcileSettings()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Compute:ReconcileWhileBuilding"] = "true",
            ["Compute:ReconcileMinAgeHours"] = "6",
            ["Compute:NarrowReads"] = "false"
        }).Build();

        var o = ComputeConfig.ReadCompute(config);

        o.ReconcileWhileBuilding.Should().BeTrue();
        o.ReconcileMinAgeHours.Should().Be(6);
        o.NarrowReads.Should().BeFalse();
    }

    // ── Fast lane: small-table fields are not stuck behind builds of hours on a big table ────────────────────

    private static FormulaPlan Formula(long table, int fid) =>
        new(table, fid, table * 1000 + fid, "1", PowerBase.Formula.Types.FormulaType.Number, [], [], false);

    private static readonly Dictionary<long, long> Rows = new() { [244] = 1_140_000, [276] = 3_000, [248] = 900_000, [245] = 400 };

    [Theory]
    [InlineData(1, 2, 0)]     // one build: nothing can be kept, one lane as before
    [InlineData(8, 2, 2)]
    [InlineData(3, 5, 2)]     // at least one build stays for big tables
    [InlineData(8, 0, 0)]     // switched off
    public void FastLaneReserved_LeavesAtLeastOneBuildForBigTables(int builds, int fastLane, int expected)
    {
        var o = new ComputeOptions { BackgroundSessionsPerDatabase = 15, BuildsPerReplica = builds, FastLaneBuilds = fastLane };
        o.FastLaneReserved.Should().Be(expected);
    }

    [Fact]
    public void IsFastLane_SmallTablesOnly_ASummaryOverABigChildIsBig()
    {
        MaterializeBuilder.IsFastLane(Formula(276, 10), Rows, 100_000).Should().BeTrue("Client Weeks has 3,000 rows");
        MaterializeBuilder.IsFastLane(Formula(244, 10), Rows, 100_000).Should().BeFalse("Sessions has 1.1 M rows");
        MaterializeBuilder.IsFastLane(null, Rows, 100_000).Should().BeTrue("a field with no plan is only marked, nothing is built");
        MaterializeBuilder.IsFastLane(Formula(999, 1), Rows, 100_000).Should().BeTrue("a table with no statistics yet is empty");

        // Summary on DSS (400 rows) over Status Logs (900 k rows): the cost is the child table.
        var summary = new SummaryPlan(245, 30, 245_030, 248, 6, "Count", null, null, null);
        MaterializeBuilder.IsFastLane(summary, Rows, 100_000).Should().BeFalse();
        // Lookup on Client Weeks (small) from DSS (small): fast.
        var lookup = new LookupPlan(276, 37, 276_037, 12, 245, 6, "f_6");
        MaterializeBuilder.IsFastLane(lookup, Rows, 100_000).Should().BeTrue();
        // A projected field is judged by its shape.
        var projected = new ProjectedPlan(276, 40, 276_040, "INT", false, new SummaryPlan(276, 40, 276_040, 244, 9, "Count", null, null, null));
        MaterializeBuilder.IsFastLane(projected, Rows, 100_000).Should().BeFalse();
    }

    [Fact]
    public void SplitLanes_KeepsThePriorityOrderInsideEachLane()
    {
        var ordered = new[] { Formula(244, 1), Formula(276, 2), Formula(244, 3), Formula(245, 4), Formula(276, 5) };
        var o = new ComputeOptions { BackgroundSessionsPerDatabase = 15, BuildsPerReplica = 8, FastLaneBuilds = 2 };

        var (fast, big) = MaterializeBuilder.SplitLanes(ordered, p => p, Rows, o);

        fast.Select(p => p.Fid).Should().Equal(2, 4, 5);
        big.Select(p => p.Fid).Should().Equal(1, 3);
    }

    [Fact]
    public void SplitLanes_NoReservedBuilds_IsOneLaneAsBefore()
    {
        var ordered = new[] { Formula(244, 1), Formula(276, 2) };
        var (fast, big) = MaterializeBuilder.SplitLanes(ordered, p => p, Rows, new ComputeOptions { BackgroundSessionsPerDatabase = 1 });

        fast.Should().BeEmpty();
        big.Select(p => p.Fid).Should().Equal(1, 2);
    }

    [Theory]
    [InlineData(16, 15, false, 0, 1)]   // live changes may take any slot
    [InlineData(16, 15, true, 0, 2)]    // background: slot 1 stays for live changes
    [InlineData(16, 15, true, 2, 4)]    // a big build also leaves the fast lane's two slots
    [InlineData(16, 15, true, 40, 16)]  // never more than every slot: the highest stays available
    [InlineData(2, 1, true, 1, 2)]
    public void FirstSlot_BigBuildsLeaveTheFastLaneSlotsFree(int max, int background, bool backgroundOnly, int keepFree, int expected)
    {
        var o = new ComputeOptions { MaxSessionsPerDatabase = max, BackgroundSessionsPerDatabase = background };
        DatabaseGuard.FirstSlot(o, backgroundOnly, keepFree).Should().Be(expected);
    }

    [Fact]
    public void ParentKeyChunks_SplitLongListsIntoSortedChunksOfAtMostAThousand()
    {
        var keys = Enumerable.Range(1, 2_500).Reverse().Select(i => (object)(long)i).ToList();

        var chunks = PowerBase.Infrastructure.Repositories.RecordRepository.ParentKeyChunks(keys);

        chunks.Select(c => c.Count).Should().Equal(1000, 1000, 500);
        chunks.SelectMany(c => c).Cast<long>().Should().BeInAscendingOrder().And.HaveCount(2_500).And.OnlyHaveUniqueItems();
        PowerBase.Infrastructure.Repositories.RecordRepository.ParentKeyChunks(keys.Take(1000).ToList()).Should().HaveCount(1, "a short list stays one query");
    }

    [Fact]
    public void BuildOnlyApps_LimitsOnlyTheTenantsNamed()
    {
        var o = ComputeConfig.ReadCompute(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Compute:BuildOnlyApps"] = "17:5, 17:9, 4:2"
        }).Build());

        o.BuildOnlyAppsFor(17).Should().BeEquivalentTo(new long[] { 5, 9 });
        o.BuildOnlyAppsFor(4).Should().BeEquivalentTo(new long[] { 2 });
        o.BuildOnlyAppsFor(45).Should().BeNull("a tenant not named builds every app");
        new ComputeOptions().BuildOnlyAppsFor(17).Should().BeNull();
    }

    [Fact]
    public void Config_ReadsTheFastLaneSettings()
    {
        new ComputeOptions().FastLaneBuilds.Should().Be(2);
        new ComputeOptions().FastLaneMaxRows.Should().Be(100_000);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Compute:FastLaneBuilds"] = "3",
            ["Compute:FastLaneMaxRows"] = "50000"
        }).Build();

        var o = ComputeConfig.ReadCompute(config);

        o.FastLaneBuilds.Should().Be(3);
        o.FastLaneMaxRows.Should().Be(50_000);
    }
}
