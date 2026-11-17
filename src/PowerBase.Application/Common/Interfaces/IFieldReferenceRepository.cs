using PowerBase.Application.FieldReferences;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>A dashboard page as the index reads it: just what extraction needs.</summary>
public sealed record DashboardSource(long Id, Guid PublicId, long AppId, string Name, string Definition);

/// <summary>One role's Custom Rules row for one table. <paramref name="Kind"/> is "View" or "Modify".</summary>
public sealed record CustomRuleSource(long RoleId, long TableId, string Kind, string FilterJson);

/// <summary>A live relationship as the index reads it. The Id fields are meta.AppField.Id values.</summary>
public sealed record RelationshipSource(
    long Id, Guid PublicId, long ParentTableId, long ChildTableId,
    long ReferenceFieldId, long? ProxyFieldId, long? DisplayKeyFieldId);

/// <summary>A live PowerFlow with its live steps, as the index reads them.</summary>
public sealed record PipelineSource(long Id, Guid PublicId, long AppId, string Name, IReadOnlyList<PipelineStepSnapshot> Steps);

public interface IFieldReferenceRepository
{
    /// <summary>Atomically replaces every row of one source with <paramref name="rows"/> (empty = remove them all).</summary>
    Task ReplaceForSourceAsync(string sourceType, long sourceId, IReadOnlyCollection<FieldReferenceRow> rows, CancellationToken ct = default);

    /// <summary>Adds rows without touching the source's other rows — for a table rebuild, where
    /// <see cref="ClearTableAsync"/> has already removed that table's rows and a source spanning several tables
    /// (a dashboard, a role's rules) must not lose the rows it has on the others.</summary>
    Task InsertRowsAsync(IReadOnlyCollection<FieldReferenceRow> rows, CancellationToken ct = default);

    /// <summary>Removes a source's rows by its public id — works after the source was soft-deleted.</summary>
    Task DeleteBySourcePublicIdAsync(string sourceType, Guid sourcePublicId, CancellationToken ct = default);

    /// <summary>Every live report of the table — including other users' personal ones and the hidden
    /// Default Report Settings row, which the user-facing list queries filter out. Only Id, PublicId,
    /// AppTableId, Name and Definition are populated.</summary>
    Task<IReadOnlyList<Report>> ListReportSourcesAsync(long tableId, CancellationToken ct = default);

    /// <summary>Removes the rows of every rule of the form, by the form's public id.</summary>
    Task DeleteRuleRowsOfFormAsync(Guid formPublicId, CancellationToken ct = default);

    /// <summary>Every reference to <paramref name="targetFieldId"/> whose source still exists (not soft-deleted).</summary>
    Task<IReadOnlyList<FieldReferenceItem>> ListByTargetFieldAsync(long targetFieldId, CancellationToken ct = default);

    /// <summary>True when the table was last indexed with the current <see cref="FieldReferenceIndexVersion.Current"/> or newer.</summary>
    Task<bool> IsTableIndexedAsync(long tableId, CancellationToken ct = default);

    /// <summary>Of <paramref name="tableIds"/>, the ones already indexed at the current version — one query instead of one per table.</summary>
    Task<IReadOnlySet<long>> ListIndexedTableIdsAsync(IReadOnlyCollection<long> tableIds, CancellationToken ct = default);

    Task MarkTableIndexedAsync(long tableId, CancellationToken ct = default);

    /// <summary>Removes every row whose source lives on the table, plus its indexed marker.</summary>
    Task ClearTableAsync(long tableId, CancellationToken ct = default);

    // ── Source readers for the newer kinds of source ────────────────────────────────────────────
    // Read-only queries over other modules' tables, kept here so indexing them needs no change to those modules' repositories.

    Task<DashboardSource?> GetDashboardSourceAsync(Guid pagePublicId, CancellationToken ct = default);
    Task<IReadOnlyList<DashboardSource>> ListDashboardSourcesAsync(long appId, CancellationToken ct = default);

    /// <summary>Report public id → the table the report runs on (live reports only).</summary>
    Task<IReadOnlyDictionary<Guid, long>> GetReportTableIdsAsync(IReadOnlyCollection<Guid> reportPublicIds, CancellationToken ct = default);

    Task<long?> GetRoleIdAsync(Guid rolePublicId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomRuleSource>> ListCustomRulesOfRoleAsync(long roleId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomRuleSource>> ListCustomRulesOfTableAsync(long tableId, CancellationToken ct = default);

    Task<RelationshipSource?> GetRelationshipSourceAsync(Guid relationshipPublicId, CancellationToken ct = default);
    Task<IReadOnlyList<RelationshipSource>> ListRelationshipsOfChildTableAsync(long childTableId, CancellationToken ct = default);

    /// <summary>A live (not deleted) PowerFlow with its live steps; null when it is gone.</summary>
    Task<PipelineSource?> GetPipelineSourceAsync(Guid pipelinePublicId, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineSource>> ListPipelineSourcesAsync(long appId, CancellationToken ct = default);

    /// <summary>The PowerFlows (public ids) created, edited, deleted or whose steps changed at or after <paramref name="since"/>, plus the
    /// database clock at the time of the scan. <paramref name="since"/> is null the first time: every PowerFlow.</summary>
    Task<(IReadOnlyList<Guid> Changed, DateTime ScannedUpTo)> ListPipelinesChangedSinceAsync(DateTime? since, CancellationToken ct = default);

    /// <summary>A live saved import; null when it is gone.</summary>
    Task<ImportDefinition?> GetImportDefinitionAsync(Guid importPublicId, CancellationToken ct = default);
    Task<IReadOnlyList<ImportDefinition>> ListImportDefinitionsAsync(long appId, CancellationToken ct = default);

    Task<DateTime?> GetPipelineSyncWatermarkAsync(CancellationToken ct = default);
    Task SetPipelineSyncWatermarkAsync(DateTime syncedUpTo, CancellationToken ct = default);
}

/// <summary>Which generation of the index a table was built with. Bump when the index learns a new kind of source,
/// so tables built earlier are rebuilt once (see migration 094).</summary>
public static class FieldReferenceIndexVersion
{
    /// <summary>1 = reports, forms, form rules, fields. 2 = + relationships, dashboards, custom rules. 3 = + PowerFlows. 4 = + imports.</summary>
    public const int Current = 4;
}
