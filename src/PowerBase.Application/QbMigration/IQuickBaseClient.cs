namespace PowerBase.Application.QbMigration;

/// <summary>Read-only access to one QuickBase realm. The implementation owns rate limiting (shared token bucket),
/// retry-after handling and backoff, so callers just ask for data.</summary>
public interface IQuickBaseClient
{
    Task<IReadOnlyList<QbTable>> GetTablesAsync(string appId, CancellationToken ct = default);

    Task<IReadOnlyList<QbField>> GetFieldsAsync(string tableId, CancellationToken ct = default);

    /// <summary>One page of records. <paramref name="where"/> is QuickBase query syntax, e.g. <c>{3.GTE.100}AND{3.LT.200}</c>.</summary>
    Task<QbQueryPage> QueryAsync(string tableId, IReadOnlyList<int> select, string? where, int skip, int? top, CancellationToken ct = default);
}

/// <summary>Raised for an answer that retrying will not fix (HTTP 4xx other than 429): the chunk goes to the dead-letter queue.</summary>
public sealed class QuickBaseRequestException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
