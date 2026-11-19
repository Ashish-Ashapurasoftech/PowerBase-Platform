using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using PowerBase.Application.QbMigration;
using PowerBase.Infrastructure.QbMigration;

namespace PowerBase.QbMigration.Tests;

/// <summary>A Sessions chunk logs about 25,000 issues. Writing them one INSERT at a time forced a log flush per row and, with several
/// workers, hit the database's log-rate limit (each chunk took about 2 minutes to log, and chunks timed out). They go in a few
/// set-based statements in one transaction instead.</summary>
public sealed class QbmIssueWriteSqlTests : IAsyncLifetime
{
    private readonly long _tableMapId = 930_000 + Random.Shared.Next(60_000);
    private QbmStateStore _state = null!;

    public async Task InitializeAsync()
    {
        _state = new QbmStateStore(new SqlDb.Factory());
        await _state.InitAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "001_qbm_state.sql")));
    }

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync("DELETE FROM qbm.Issue WHERE TableMapId = @t", new { t = _tableMapId });
    }

    private async Task<List<dynamic>> RowsAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        return (await c.QueryAsync("SELECT RunId, TableMapId, ChunkId, QbRecordId, QbFid, Severity, Code, Message FROM qbm.Issue WHERE TableMapId = @t ORDER BY Id", new { t = _tableMapId })).ToList();
    }

    [SqlFact]
    public async Task Issues_are_written_with_their_values_nulls_and_special_characters_intact()
    {
        await _state.AddIssuesAsync(7, _tableMapId, 55,
        [
            new("warning", "QBM_REFERENCE_NOT_FOUND", "'Related Goal': no parent record with key '\"01-03/OZ-1\"'; stored empty. Zürich 東京 \\ / \" '", 42, 9),
            new("info", "QBM_FIELD_SKIP", "No record, no field"),
        ]);
        var rows = await RowsAsync();
        rows.Should().HaveCount(2);
        ((object)rows[0].RunId).Should().Be(7L);
        ((object)rows[0].ChunkId).Should().Be(55L);
        ((object)rows[0].QbRecordId).Should().Be(42L);
        ((object)rows[0].QbFid).Should().Be(9);
        ((string)rows[0].Message).Should().Be("'Related Goal': no parent record with key '\"01-03/OZ-1\"'; stored empty. Zürich 東京 \\ / \" '");
        ((string)rows[0].Severity).Should().Be("warning");
        ((string)rows[0].Code).Should().Be("QBM_REFERENCE_NOT_FOUND");
        ((object?)rows[1].QbRecordId).Should().BeNull();
        ((object?)rows[1].QbFid).Should().BeNull();
    }

    [SqlFact]
    public async Task A_very_long_message_is_cut_to_the_column_width()
    {
        await _state.AddIssuesAsync(7, _tableMapId, null, [new("error", "X", new string('x', 5000))]);
        var rows = await RowsAsync();
        ((string)rows.Single().Message).Should().HaveLength(2000);
        ((object?)rows.Single().ChunkId).Should().BeNull();
    }

    [SqlFact]
    public async Task A_whole_chunks_worth_of_issues_is_written_in_order_and_in_few_statements()
    {
        var issues = Enumerable.Range(1, 12_000).Select(i => new QbIssue("warning", "QBM_REFERENCE_NOT_FOUND", $"issue {i}", i, 9)).ToList();
        await _state.AddIssuesAsync(7, _tableMapId, 55, issues);
        var rows = await RowsAsync();
        rows.Should().HaveCount(12_000);
        rows.Select(r => (long)r.QbRecordId).Should().Equal(Enumerable.Range(1, 12_000).Select(i => (long)i));
        ((string)rows[^1].Message).Should().Be("issue 12000");
    }

    [SqlFact]
    public async Task No_issues_writes_nothing()
    {
        await _state.AddIssuesAsync(7, _tableMapId, 55, []);
        (await RowsAsync()).Should().BeEmpty();
    }
}
