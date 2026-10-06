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
}

/// <summary>Does nothing — the default for code that builds a handler without an indexer (unit tests).</summary>
public sealed class NullFieldReferenceIndexer : IFieldReferenceIndexer
{
    public static readonly NullFieldReferenceIndexer Instance = new();

    public Task ReindexReportAsync(Guid reportPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexFormAsync(Guid formPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexFormRuleAsync(Guid rulePublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexFieldAsync(Guid fieldPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReindexTableFieldsAsync(long tableId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveSourceAsync(string sourceType, Guid sourcePublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFormAsync(Guid formPublicId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RebuildTableAsync(long tableId, CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureTableIndexedAsync(long tableId, CancellationToken ct = default) => Task.CompletedTask;
}
