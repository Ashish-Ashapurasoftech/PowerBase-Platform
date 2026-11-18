namespace PowerBase.Application.FieldReferences;

/// <summary>
/// Keeps meta.FieldReference in step with the things that reference fields. Command handlers call
/// it after they save (a Reindex* replaces every row of that one source) or delete (Remove*), so
/// the Usage tab never has to scan report JSON or compile formulas to answer "what uses this?".
///
/// The index is derived data: a failure here must never fail the user's save, so implementations
/// log and swallow errors — <see cref="RebuildTableAsync"/> is the repair path.
/// </summary>
public interface IFieldReferenceIndexer
{
    Task ReindexReportAsync(Guid reportPublicId, CancellationToken ct = default);

    /// <summary>The form's elements plus the rules defined on it (their action targets resolve through the layout).</summary>
    Task ReindexFormAsync(Guid formPublicId, CancellationToken ct = default);

    Task ReindexFormRuleAsync(Guid rulePublicId, CancellationToken ct = default);

    /// <summary>Formula / Lookup / Summary / Report Link / Action Button settings of one field.</summary>
    Task ReindexFieldAsync(Guid fieldPublicId, CancellationToken ct = default);

    /// <summary>The fields a relationship stands on: reference field, the key / display key it matches on, its display label.</summary>
    Task ReindexRelationshipAsync(Guid relationshipPublicId, CancellationToken ct = default);

    /// <summary>The fields a dashboard binds its filters to and the fields its Search widgets search.</summary>
    Task ReindexDashboardAsync(Guid pagePublicId, CancellationToken ct = default);

    /// <summary>Every Custom Rule (View and Modify, all tables) of one role — they are always saved together.</summary>
    Task ReindexCustomRulesAsync(Guid rolePublicId, CancellationToken ct = default);

    /// <summary>Every field of the table — for handlers that create/delete several fields at once.</summary>
    Task ReindexTableFieldsAsync(long tableId, CancellationToken ct = default);

    /// <summary>Drops what a deleted report/form/rule/field referenced. <paramref name="sourcePublicId"/> may be soft-deleted already.</summary>
    Task RemoveSourceAsync(string sourceType, Guid sourcePublicId, CancellationToken ct = default);

    /// <summary>Drops the rows of a deleted form and of every rule that belonged to it.</summary>
    Task RemoveFormAsync(Guid formPublicId, CancellationToken ct = default);

    /// <summary>Re-reads every report, form, rule and field of the table and rewrites its rows.
    /// Backfills tables that predate the index and repairs drift.</summary>
    Task RebuildTableAsync(long tableId, CancellationToken ct = default);

    /// <summary>Runs <see cref="RebuildTableAsync"/> once, the first time a table is asked about.</summary>
    Task EnsureTableIndexedAsync(long tableId, CancellationToken ct = default);

    /// <summary><see cref="EnsureTableIndexedAsync"/> for many tables at once: one query finds the ones that need it,
    /// and only those are rebuilt. A table that fails to rebuild is logged and skipped; the rest still run.</summary>
    Task EnsureTablesIndexedAsync(IReadOnlyCollection<long> tableIds, CancellationToken ct = default);

    /// <summary>The fields one PowerFlow's steps use (replaces every row of that pipeline; a deleted pipeline loses its rows).</summary>
    Task ReindexPipelineAsync(Guid pipelinePublicId, CancellationToken ct = default);

    /// <summary>The fields a saved import writes, matches on, reads, filters on and calculates from (replaces every row of that import).</summary>
    Task ReindexImportAsync(Guid importPublicId, CancellationToken ct = default);

    /// <summary>Re-indexes every PowerFlow created, edited or deleted since the last sync (all of them the first time). PowerFlows are
    /// saved by another module inside its own transaction, so they are picked up by this scan — run by a background worker and again
    /// before a Usage answer — instead of by a call from the save. Returns how many were re-indexed.</summary>
    Task<int> SyncPipelinesAsync(CancellationToken ct = default);
}

/// <summary>Does nothing — the default for code that builds a handler without an indexer (unit tests).</summary>
public sealed class NullFieldReferenceIndexer : IFieldReferenceIndexer
{
    public static readonly NullFieldReferenceIndexer Instance = new();

    public Task ReindexReportAsync(Guid reportPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexFormAsync(Guid formPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexFormRuleAsync(Guid rulePublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexFieldAsync(Guid fieldPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexRelationshipAsync(Guid relationshipPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexDashboardAsync(Guid pagePublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexCustomRulesAsync(Guid rolePublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexTableFieldsAsync(long tableId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveSourceAsync(string sourceType, Guid sourcePublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFormAsync(Guid formPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RebuildTableAsync(long tableId, CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureTableIndexedAsync(long tableId, CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureTablesIndexedAsync(IReadOnlyCollection<long> tableIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexImportAsync(Guid importPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexPipelineAsync(Guid pipelinePublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> SyncPipelinesAsync(CancellationToken ct = default) => Task.FromResult(0);
}
