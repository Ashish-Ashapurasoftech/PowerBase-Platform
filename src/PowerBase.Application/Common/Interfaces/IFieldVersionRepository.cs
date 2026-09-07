using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>Append-only store for field audit/version history (meta.AppFieldVersion /
/// meta.AppFieldVersionChange) — see PowerBase.Application.Fields.Versioning.FieldVersionService,
/// the only writer. No Update/Delete methods exist here by design: history is never rewritten.</summary>
public interface IFieldVersionRepository
{
    /// <summary>Next version number for a field (1 if it has none yet). Must be called within the
    /// same transaction that inserts the new version, to keep numbering race-free alongside the
    /// UQ_AppFieldVersion_Field_Version constraint.</summary>
    Task<int> GetNextVersionNumberAsync(long appFieldId, IDbTransaction transaction, CancellationToken ct = default);

    /// <summary>Current (highest) version number for a field, or 0 if it has never been versioned.</summary>
    Task<int> GetCurrentVersionNumberAsync(long appFieldId, CancellationToken ct = default);

    Task InsertVersionAsync(AppFieldVersion version, IReadOnlyList<Fields.Versioning.FieldChangeEntry> changes,
        IDbTransaction transaction, CancellationToken ct = default);

    Task<(IReadOnlyList<FieldVersionListItem> Items, int Total)> ListByFieldAsync(
        long appFieldId, int page, int pageSize, CancellationToken ct = default);

    Task<AppFieldVersion?> GetByFieldAndVersionAsync(long appFieldId, int version, CancellationToken ct = default);

    Task<IReadOnlyList<AppFieldVersionChange>> ListChangesAsync(long appFieldVersionId, CancellationToken ct = default);
}

/// <summary>One row for the Audit History grid — the version's own columns plus the joined
/// changed-by display name and a short summary of which properties changed (the grid never shows
/// full before/after values; see GetFieldVersionDetailQuery for that).</summary>
public class FieldVersionListItem
{
    public int Version { get; set; }
    public FieldVersionChangeType ChangeType { get; set; }
    public int? RestoredFromVersion { get; set; }
    public string CommitMessage { get; set; } = string.Empty;
    public string ChangedByName { get; set; } = string.Empty;
    public DateTime ChangedOn { get; set; }
    public string ChangedPropertiesSummary { get; set; } = string.Empty;
}
