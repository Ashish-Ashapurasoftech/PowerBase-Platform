using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.Versioning;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Queries.GetFieldVersionDetail;

public class FieldVersionDetailResult
{
    public int Version { get; init; }
    public string CommitMessage { get; init; } = string.Empty;
    public string ChangedByName { get; init; } = string.Empty;
    public DateTime ChangedOn { get; init; }
    public int? RestoredFromVersion { get; init; }
    public bool IsCurrent { get; init; }
    /// <summary>The field's current (highest) version number, regardless of which version this
    /// result is for — lets the restore-preview dialog show "Current Version: N" without a second
    /// request.</summary>
    public int CurrentVersion { get; init; }
    public IReadOnlyList<FieldChangeEntry> Changes { get; init; } = [];
    /// <summary>What would change if this version were restored right now — diffed against the
    /// field's LIVE current settings (not against this version's own predecessor, which is what
    /// <see cref="Changes"/> shows). Powers the restore confirmation's change preview. Empty (and
    /// meaningless) when <see cref="IsCurrent"/> is true.</summary>
    public IReadOnlyList<FieldChangeEntry> ChangesFromCurrent { get; init; } = [];
}

/// <summary>Full per-property before/after for one version — backs the Audit History tab's
/// "View Details" action (requirement: don't show every value in the main grid, only on demand).</summary>
public class GetFieldVersionDetailQueryHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IFieldVersionRepository _versionRepo;

    public GetFieldVersionDetailQueryHandler(IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IFieldVersionRepository versionRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _versionRepo = versionRepo;
    }

    public async Task<FieldVersionDetailResult> HandleAsync(GetFieldVersionDetailQuery query, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var field = await _fieldRepo.GetByPublicIdAsync(query.FieldPublicId, ct)
            ?? throw new NotFoundException("Field", query.FieldPublicId);
        if (field.AppTableId != table.Id)
            throw new NotFoundException("Field", query.FieldPublicId);

        var version = await _versionRepo.GetByFieldAndVersionAsync(field.Id, query.Version, ct)
            ?? throw new NotFoundException("FieldVersion", query.Version);

        var changeRows = await _versionRepo.ListChangesAsync(version.Id, ct);

        var currentVersionNumber = await _versionRepo.GetCurrentVersionNumberAsync(field.Id, ct);
        var isCurrent = query.Version == currentVersionNumber;

        var changesFromCurrent = isCurrent
            ? []
            : FieldSnapshotDiffer.Diff(FieldSnapshot.From(field), FieldSnapshot.FromJson(version.SnapshotJson));

        return new FieldVersionDetailResult
        {
            Version = version.Version,
            CommitMessage = version.CommitMessage,
            ChangedByName = version.ChangedByName,
            ChangedOn = version.ChangedOn,
            RestoredFromVersion = version.RestoredFromVersion,
            IsCurrent = isCurrent,
            CurrentVersion = currentVersionNumber,
            Changes = changeRows.Select(c => new FieldChangeEntry(c.PropertyName, c.OldValue, c.NewValue)).ToList(),
            ChangesFromCurrent = changesFromCurrent,
        };
    }
}
