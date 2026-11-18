namespace PowerBase.Application.QbMigration;

public enum QbConflictKind { CreatedInPowerBase, EditedInPowerBase, DeletedInPowerBase }

/// <summary>A PowerBase record that changed in PowerBase after the load; the final sync would overwrite or lose that change.</summary>
public sealed record QbConflict(long RecordId, QbConflictKind Kind);

/// <summary>A PowerBase row as the conflict check needs it.</summary>
public sealed record QbPbRowState(long Id, Guid PublicId, bool IsDeleted, DateTime? ModifiedOn, DateTime? CreatedOn);

/// <summary>Finds records changed in PowerBase since the load (or the last sync). QuickBase is the source until switch-over, so
/// the final sync overwrites PowerBase: these are the changes it would lose. The signs, each exact on its own:
/// <list type="bullet">
/// <item>Created: every migrated record has the stable PublicId of its table and Record ID; a record PowerBase created has another.</item>
/// <item>Deleted: a migrated record is only ever marked deleted by the sync, and only when QuickBase no longer has it.</item>
/// <item>Edited: PowerBase's activity log names the record; or its Date Modified is later than QuickBase's (a migrated record carries
/// QuickBase's Date Modified, a PowerBase edit stamps its own time).</item>
/// </list></summary>
public static class QbSyncConflicts
{
    /// <summary>Clock difference tolerated between PowerBase's and QuickBase's Date Modified.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    /// <param name="rows">PowerBase rows that are deleted, or created or modified since <paramref name="sinceUtc"/>.</param>
    /// <param name="stablePublicId">The PublicId the migration gives a Record ID.</param>
    /// <param name="qbModified">QuickBase's Date Modified of those Record IDs that QuickBase has (null when unreadable); a Record ID
    /// that isn't a key is not in QuickBase.</param>
    /// <param name="auditedEdits">PublicIds the activity log shows created or edited in PowerBase since <paramref name="sinceUtc"/>.</param>
    public static List<QbConflict> Find(IEnumerable<QbPbRowState> rows, Func<long, Guid> stablePublicId,
        IReadOnlyDictionary<long, DateTime?> qbModified, IReadOnlySet<Guid> auditedEdits, DateTime sinceUtc)
    {
        var conflicts = new List<QbConflict>();
        foreach (var row in rows.OrderBy(r => r.Id))
        {
            var inQb = qbModified.TryGetValue(row.Id, out var qbDate);
            if (row.PublicId != stablePublicId(row.Id))
            {
                // Created in PowerBase then deleted there: nothing left to lose.
                if (!row.IsDeleted) conflicts.Add(new(row.Id, QbConflictKind.CreatedInPowerBase));
            }
            else if (row.IsDeleted)
            {
                if (inQb) conflicts.Add(new(row.Id, QbConflictKind.DeletedInPowerBase));
            }
            else if (auditedEdits.Contains(row.PublicId)
                     || (row.ModifiedOn is { } pbDate && pbDate >= sinceUtc && (!inQb || qbDate is null || pbDate > qbDate.Value + Tolerance)))
            {
                conflicts.Add(new(row.Id, QbConflictKind.EditedInPowerBase));
            }
        }
        return conflicts;
    }

    public static string Describe(QbConflictKind kind) => kind switch
    {
        QbConflictKind.CreatedInPowerBase => "created in PowerBase",
        QbConflictKind.EditedInPowerBase => "edited in PowerBase",
        _ => "deleted in PowerBase",
    };
}
