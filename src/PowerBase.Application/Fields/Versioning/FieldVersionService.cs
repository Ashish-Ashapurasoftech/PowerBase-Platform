using System.Data;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Fields.Versioning;

/// <summary>The one place a new field version gets created — called by both
/// UpdateFieldCommandHandler and RestoreFieldVersionCommandHandler so neither duplicates the
/// diff-then-insert logic. A version is only ever inserted when the diff is non-empty (requirement:
/// a no-op save must not create a new version) and only ever inserted, never mutated.</summary>
public class FieldVersionService
{
    private readonly IFieldVersionRepository _versionRepo;
    private readonly IQueryContext _queryContext;

    public FieldVersionService(IFieldVersionRepository versionRepo, IQueryContext queryContext)
    {
        _versionRepo = versionRepo;
        _queryContext = queryContext;
    }

    /// <summary>Diffs <paramref name="before"/> against <paramref name="after"/> and, only if they
    /// differ, inserts a new AppFieldVersion (+ its AppFieldVersionChange rows) within
    /// <paramref name="transaction"/>. Returns the new version number, or null if nothing changed.
    /// Callers must still commit/rollback <paramref name="transaction"/> themselves — this method
    /// only adds statements to it.</summary>
    public async Task<int?> CreateVersionIfChangedAsync(
        long appFieldId,
        FieldSnapshot before,
        FieldSnapshot after,
        string commitMessage,
        FieldVersionChangeType changeType,
        int? restoredFromVersion,
        IDbTransaction transaction,
        CancellationToken ct = default)
    {
        var changes = FieldSnapshotDiffer.Diff(before, after);
        if (changes.Count == 0) return null;

        var nextVersion = await _versionRepo.GetNextVersionNumberAsync(appFieldId, transaction, ct);

        var version = new AppFieldVersion
        {
            AppFieldId = appFieldId,
            Version = nextVersion,
            ChangeType = changeType,
            RestoredFromVersion = restoredFromVersion,
            CommitMessage = commitMessage,
            ChangedByUserId = _queryContext.UserId,
            ChangedByName = _queryContext.UserName,
            ChangedOn = DateTime.UtcNow,
            SnapshotJson = after.ToJson(),
        };

        await _versionRepo.InsertVersionAsync(version, changes, transaction, ct);
        return nextVersion;
    }
}
