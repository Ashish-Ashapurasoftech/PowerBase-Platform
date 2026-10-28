using NSubstitute;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>
/// The checks made before a search the planner can narrow is actually answered from the index: a field changed lately, a small
/// table, an index holding fewer documents than the table has records. Each "no" sends the search to SQL, which finds every record.
/// </summary>
public class PipelineAiSearchGateTests
{
    private static long _nextTableId = 5000;   // the counts are cached per table: a table of its own for every test

    private readonly AppTable _table = new() { Id = Interlocked.Increment(ref _nextTableId), AppId = 1 };
    private readonly IAzureSearchService _azure = Substitute.For<IAzureSearchService>();
    private readonly IRecordRepository _records = Substitute.For<IRecordRepository>();
    private readonly DateTime _now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static readonly PipelineAiSearchPlan Plan = new("filter", null, [7]);

    private static List<AppField> Fields(DateTime? modifiedOn = null, DateTime? createdOn = null, string? slot = "str_01") =>
    [
        new() { Id = 7, Fid = 7, Name = "Status", TypeCode = "Text", IsSearchable = true, IsFilterable = true, SearchSlot = slot, ModifiedOn = modifiedOn, CreatedOn = createdOn ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
    ];

    private static PipelineExecutionOptions Options(int minRows = 0, bool guard = true, int settleHours = 24, int lagSeconds = 600) => new()
    {
        AiSearchMinTableRows = minRows, AiSearchCountGuard = guard, AiSearchFieldSettleHours = settleHours, AiSearchIndexLagSeconds = lagSeconds, AiSearchGuardCacheSeconds = 0
    };

    private void Counts(long sql, long index)
    {
        _records.CountAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(Task.FromResult((int)sql));
        _azure.SearchRecordsPagedAsync(Arg.Any<long>(), _table.Id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(((IReadOnlyList<Guid>)new List<Guid>(), index)));
    }

    private void Recent(int count) =>
        _records.ListRecordsChangedSinceAsync(_table, Arg.Any<DateTime>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var after = ci.ArgAt<long>(2);
                var take = ci.ArgAt<int>(4);
                return Task.FromResult<IReadOnlyList<(long Id, DateTime ChangedOn)>>(
                    Enumerable.Range(1, count).Select(i => (Id: (long)i, ChangedOn: DateTime.UtcNow)).Where(r => r.Id > after).Take(take).ToList());
            });

    private Task<PipelineAiSearchGateResult> Evaluate(List<AppField> fields, PipelineExecutionOptions options, PipelineAiSearchPlan? plan = null) =>
        PipelineAiSearchGate.EvaluateAsync(_table, fields, plan ?? Plan, _azure, 1, _records, options, CancellationToken.None, _now);

    // ── a field changed lately ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFieldChangedWithinTheSettleWindow_IsAnsweredFromSql()
    {
        var result = await Evaluate(Fields(modifiedOn: _now.AddHours(-3)), Options(guard: false));

        Assert.False(result.UseAiSearch);
        Assert.Contains("changed within the last 24 hours", result.SqlReason);
    }

    [Fact]
    public async Task AFieldCreatedWithinTheSettleWindow_CountsAsChanged()
    {
        var result = await Evaluate(Fields(modifiedOn: null, createdOn: _now.AddHours(-1)), Options(guard: false));

        Assert.False(result.UseAiSearch);
    }

    [Fact]
    public async Task AFieldChangedLongAgo_DoesNotStopTheIndex() =>
        Assert.True((await Evaluate(Fields(modifiedOn: _now.AddHours(-25)), Options(guard: false))).UseAiSearch);

    [Fact]
    public async Task OnlyTheFieldsTheFilterReadsAreChecked()
    {
        var fields = Fields(modifiedOn: _now.AddHours(-1));
        fields.Add(new AppField { Id = 8, Fid = 8, Name = "Other", TypeCode = "Text", ModifiedOn = _now.AddMinutes(-1) });

        Assert.False((await Evaluate(fields, Options(guard: false), new PipelineAiSearchPlan("f", null, [8]))).UseAiSearch);
        Assert.False((await Evaluate(fields, Options(guard: false), new PipelineAiSearchPlan("f", null, [7]))).UseAiSearch);
        Assert.True((await Evaluate(fields, Options(guard: false), new PipelineAiSearchPlan("f", null, []))).UseAiSearch);
    }

    [Fact]
    public async Task TheSettleWindowCanBeSwitchedOff() =>
        Assert.True((await Evaluate(Fields(modifiedOn: _now.AddMinutes(-1)), Options(guard: false, settleHours: 0))).UseAiSearch);

    // ── table size ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASmallTable_IsAnsweredFromSql()
    {
        Counts(sql: 500, index: 500);

        var result = await Evaluate(Fields(), Options(minRows: 20000, guard: false));

        Assert.False(result.UseAiSearch);
        Assert.Contains("500 records", result.SqlReason);
    }

    [Fact]
    public async Task ATableAtTheThreshold_UsesTheIndex()
    {
        Counts(sql: 20000, index: 20000);

        Assert.True((await Evaluate(Fields(), Options(minRows: 20000, guard: false))).UseAiSearch);
    }

    [Fact]
    public async Task WithoutAMinimumAndWithoutTheGuard_NothingIsCounted()
    {
        var result = await Evaluate(Fields(), Options(minRows: 0, guard: false));

        Assert.True(result.UseAiSearch);
        await _records.DidNotReceive().CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>());
    }

    // ── index completeness ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnIndexHoldingFewerDocumentsThanTheTableHasRecords_IsNotTrusted()
    {
        Counts(sql: 100000, index: 99990);
        Recent(0);

        var result = await Evaluate(Fields(), Options());

        Assert.False(result.UseAiSearch);
        Assert.Contains("missing some", result.SqlReason);
    }

    [Fact]
    public async Task ACompleteIndex_IsTrusted()
    {
        Counts(sql: 100000, index: 100000);
        Recent(0);

        Assert.True((await Evaluate(Fields(), Options())).UseAiSearch);
    }

    [Fact]
    public async Task AnIndexWithExtraDocuments_IsTrusted_BecauseADeletedRecordIsDroppedByTheSqlCheck()
    {
        Counts(sql: 100000, index: 100050);
        Recent(0);

        Assert.True((await Evaluate(Fields(), Options())).UseAiSearch);
    }

    [Fact]
    public async Task TheRecordsChangedWithinTheLagWindow_AreAllowedForInTheComparison()
    {
        // 30 records are new: the index may not have them yet, and every index-answered search reads them from SQL anyway.
        Counts(sql: 100030, index: 100000);
        Recent(30);

        Assert.True((await Evaluate(Fields(), Options())).UseAiSearch);
    }

    [Fact]
    public async Task MoreMissingThanTheRecentlyChanged_IsNotTrusted()
    {
        Counts(sql: 100030, index: 99990);
        Recent(30);

        Assert.False((await Evaluate(Fields(), Options())).UseAiSearch);
    }

    [Fact]
    public async Task ABulkLoadInProgress_SendsTheSearchToSql()
    {
        Counts(sql: 200000, index: 150000);
        Recent(PipelineAiSearchGate.MaxRecentTolerated + 5000);

        var result = await Evaluate(Fields(), Options());

        Assert.False(result.UseAiSearch);
        Assert.Contains("changed within the last", result.SqlReason);
    }

    [Fact]
    public async Task WithTheLagWindowOff_NoRecentRecordsAreAllowedFor()
    {
        Counts(sql: 100030, index: 100000);
        Recent(30);

        Assert.False((await Evaluate(Fields(), Options(lagSeconds: 0))).UseAiSearch);
    }

    [Fact]
    public async Task AnIndexThatDoesNotExistYet_HoldsNothing_AndIsNotTrusted()
    {
        Counts(sql: 100000, index: 0);
        Recent(0);

        Assert.False((await Evaluate(Fields(), Options())).UseAiSearch);
    }

    [Fact]
    public async Task ATableWithItsOwnIndex_IsCountedThere()
    {
        Counts(sql: 100000, index: 100000);
        Recent(0);
        var fields = Fields(slot: "f_7");   // one field stored as f_<fid>: the table is wide

        await Evaluate(fields, Options());

        await _azure.Received().SearchRecordsPagedAsync(Arg.Any<long>(), _table.Id, null, null, 1, 1, null, $"pb-tbl-{_table.Id}", Arg.Any<CancellationToken>());
    }

    // ── when a check cannot be made ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ACountThatFails_SendsTheSearchToSql()
    {
        _records.CountAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("timeout"));

        var result = await Evaluate(Fields(), Options());

        Assert.False(result.UseAiSearch);
        Assert.Contains("could not be checked", result.SqlReason);
    }

    [Fact]
    public async Task ACancelledCheck_IsNotSwallowed()
    {
        _records.CountAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns<Task<int>>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Evaluate(Fields(), Options()));
    }

    // ── the counts are reused briefly ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCounts_AreReusedWithinTheCacheWindow()
    {
        Counts(sql: 100000, index: 100000);
        Recent(0);
        var options = Options();
        options.AiSearchGuardCacheSeconds = 30;

        await Evaluate(Fields(), options);
        await Evaluate(Fields(), options);

        await _records.Received(1).CountAsync(_table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>());
    }
}
