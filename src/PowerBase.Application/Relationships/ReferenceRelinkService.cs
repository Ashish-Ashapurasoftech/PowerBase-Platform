using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>A parent record just added: its Record ID and the values it was written with, by Fid (plain, not encrypted).</summary>
public sealed record RelinkParent(long RecordId, IReadOnlyDictionary<long, object?> Values);

/// <summary>A parent record's values before an update, by Fid — to tell afterwards whether its key changed.</summary>
public sealed record RelinkSnapshot(long RecordId, IReadOnlyDictionary<long, object?> Values);

/// <summary>
/// Links child records to a parent record that did not exist when their reference was filled. A Reference converted from a text
/// field (or filled by an import) leaves a value that matched no parent record blank and keeps its text in the hidden "(original)"
/// field (see <c>ReferenceSettings.OriginalFid</c>). When a parent record with that key is added later — or an existing one's key is
/// changed to it — those children are pointed at it.
/// <para>The work is split in two. The parent's save only decides, from metadata and the values it wrote, whether a job is needed
/// and queues it to run after its commit (<see cref="IReferenceRelinkQueue"/>); it never reads the child table. ReferenceRelinkWorker
/// then runs the job (<see cref="RunAsync"/>): it reads each parent's current key, finds the children whose reference is blank
/// through the reference index, and links them in batches.</para>
/// <para>Matching is the conversion's and the import's: the relationship's key (display key, else the table's key field), surrounding
/// spaces and case ignored, numbers and dates compared as values. A key held by more than one record of a job links none of them.
/// Record ID# keys are left alone: a new record's Id is not a key anyone typed.</para>
/// </summary>
public interface IReferenceRelinkService
{
    /// <summary>The record's current values before an update, when the update writes a key some child keeps unmatched text for;
    /// null otherwise (no read is made).</summary>
    Task<RelinkSnapshot?> SnapshotAsync(
        AppTable parent, IReadOnlyList<AppField> fields, Guid recordPublicId, IEnumerable<long> writtenFids,
        IDbTransaction? transaction, CancellationToken ct = default);

    /// <summary>Called inside the save's transaction, after the write: queues a job (run once the save commits) for the records just
    /// added that hold a key some child keeps unmatched text for. Reads no child rows and never fails the save.</summary>
    Task QueueAddedAsync(AppTable parent, IReadOnlyList<RelinkParent> added, CancellationToken ct = default);

    /// <summary>Called inside the save's transaction, after the write: queues a job (run once the save commits) when the update
    /// changed a record's key to <paramref name="written"/>'s value. The same values written to several records (a mass update) give
    /// them all the same key, which no child can be linked to, so then nothing is queued. Reads no child rows and never fails the save.</summary>
    Task QueueIfKeyChangedAsync(
        AppTable parent, IReadOnlyList<RelinkSnapshot> before, IReadOnlyDictionary<long, object?> written, CancellationToken ct = default);

    /// <summary>Runs a queued job: links the children of these parent records by the key each holds now. Returns how many child rows
    /// were linked. Throws on a failure, so the job is retried; running it again is harmless (a linked row is never touched again).</summary>
    Task<int> RunAsync(long parentTableId, IReadOnlyList<long> parentRecordIds, CancellationToken ct = default);
}

public sealed class ReferenceRelinkService(
    IRelationshipRepository relationships,
    IAppTableRepository tables,
    IAppFieldRepository fields,
    IRecordRepository records,
    IAuditRepository audit,
    IMessagePublisher publisher,
    IQueryContext queryContext,
    IReferenceRelinkQueue queue,
    ILogger<ReferenceRelinkService> logger,
    ISearchOutbox? outbox = null) : IReferenceRelinkService
{
    /// <summary>Parent records whose key is read per query: well under SQL Server's 2,100 parameters per statement.</summary>
    private const int ReadBatchSize = 1000;

    /// <summary>One relationship whose children can be linked: its child table, the Reference field, the hidden field that keeps
    /// unmatched text, and the parent field that text is matched with.</summary>
    private sealed record Target(AppTable Child, AppField Reference, AppField Original, AppField Key);

    private readonly Dictionary<long, IReadOnlyList<Target>> _targets = new();

    public async Task<RelinkSnapshot?> SnapshotAsync(
        AppTable parent, IReadOnlyList<AppField> fields, Guid recordPublicId, IEnumerable<long> writtenFids,
        IDbTransaction? transaction, CancellationToken ct = default)
    {
        try
        {
            var written = writtenFids.ToHashSet();
            var targets = await TargetsAsync(parent, ct);
            if (!targets.Any(t => written.Contains(t.Key.Fid!.Value))) return null;

            var row = await records.GetByPublicIdAsync(parent, fields, recordPublicId, transaction, ct);
            var values = targets.Select(t => t.Key).DistinctBy(k => k.Id)
                .ToDictionary(k => (long)k.Fid!.Value, k => row.GetValueOrDefault(PhysicalNaming.GetPhysicalColumnName(k)));
            return new RelinkSnapshot(Convert.ToInt64(row["Id"]), values);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the key of {Table} record {Record} before its update failed; its children will not be linked.",
                parent.Name, recordPublicId);
            return null;
        }
    }

    public async Task QueueAddedAsync(AppTable parent, IReadOnlyList<RelinkParent> added, CancellationToken ct = default)
    {
        if (added.Count == 0) return;
        try
        {
            var keyFids = KeyFids(await TargetsAsync(parent, ct));
            if (keyFids.Count == 0) return;

            var ids = added.Where(r => keyFids.Any(fid => KeyText(r.Values.GetValueOrDefault(fid)) is not null))
                .Select(r => r.RecordId).ToList();
            if (ids.Count > 0) await queue.EnqueueAfterCommitAsync(parent.Id, ids, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Linking these children follows from the save but is never a condition of it: the record is saved either way, and the
            // children keep their "(original)" text (still shown in the grid) until a later change of the record's key queues them.
            logger.LogWarning(ex, "Queueing the re-link of child records to new {Table} records failed.", parent.Name);
        }
    }

    public async Task QueueIfKeyChangedAsync(
        AppTable parent, IReadOnlyList<RelinkSnapshot> before, IReadOnlyDictionary<long, object?> written, CancellationToken ct = default)
    {
        if (before.Count == 0) return;
        try
        {
            var keyFids = KeyFids(await TargetsAsync(parent, ct));
            if (keyFids.Count == 0) return;

            // Only a key this write set to a new, non-blank value: a child already had its chance to match the key the record held before.
            var changed = before
                .Where(r => keyFids.Any(fid => written.TryGetValue(fid, out var now) && KeyText(now) is not null
                                               && !SameKey(r.Values.GetValueOrDefault(fid), now)))
                .Select(r => r.RecordId).ToList();
            if (changed.Count == 1) await queue.EnqueueAfterCommitAsync(parent.Id, changed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Queueing the re-link of child records to changed {Table} records failed.", parent.Name);
        }
    }

    public async Task<int> RunAsync(long parentTableId, IReadOnlyList<long> parentRecordIds, CancellationToken ct = default)
    {
        if (parentRecordIds.Count == 0) return 0;

        AppTable parent;
        try { parent = await tables.GetByIdAsync(parentTableId, ct); }
        catch (NotFoundException) { return 0; } // the table was deleted since the job was queued: nothing to link

        var targets = await TargetsAsync(parent, ct);
        if (targets.Count == 0) return 0;

        // Each parent's key as it is now: it may have changed again, or the record been deleted (then it is not read), since the job
        // was queued. The repository returns the value decrypted.
        var valuesById = new Dictionary<long, Dictionary<long, object?>>();
        foreach (var key in targets.Select(t => t.Key).DistinctBy(k => k.Id))
            foreach (var ids in parentRecordIds.Distinct().Chunk(ReadBatchSize))
                foreach (var (id, value) in await records.GetColumnValuesByIdsAsync(parent, PhysicalNaming.GetPhysicalColumnName(key), ids, ct))
                {
                    if (!valuesById.TryGetValue(id, out var byFid)) valuesById[id] = byFid = new Dictionary<long, object?>();
                    byFid[key.Fid!.Value] = value;
                }
        if (valuesById.Count == 0) return 0;

        var parents = valuesById.Select(p => new RelinkParent(p.Key, p.Value)).ToList();
        var linked = 0;
        foreach (var target in targets)
            linked += await RelinkTargetAsync(parent, target, parents, ct);
        return linked;
    }

    private async Task<int> RelinkTargetAsync(AppTable parent, Target target, IReadOnlyList<RelinkParent> parents, CancellationToken ct)
    {
        var keyFid = (long)target.Key.Fid!.Value;
        var byText = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        var byValue = new Dictionary<object, long?>();
        foreach (var record in parents)
        {
            if (!record.Values.TryGetValue(keyFid, out var raw) || KeyText(raw) is not { } text) continue;
            Add(byText, text, record.RecordId);
            if (KeyFieldResolver.ConvertToColumnType(target.Key, text) is { } typed && typed is not string) Add(byValue, typed, record.RecordId);
        }
        if (byText.Count == 0) return 0;

        var unlinked = await records.ListUnlinkedOriginalsAsync(target.Child, target.Reference, target.Original, transaction: null, ct);
        var parentIdByRow = new Dictionary<long, long>();
        foreach (var (rowId, text) in unlinked)
        {
            if (Match(target.Key, text.Trim(), byText, byValue) is not long parentId) continue;
            if (target.Child.Id == parent.Id && parentId == rowId) continue; // a record of a self-relationship is never its own parent
            parentIdByRow[rowId] = parentId;
        }
        if (parentIdByRow.Count == 0) return 0;

        var changed = await records.LinkUnlinkedReferencesAsync(target.Child, target.Reference, target.Original, parentIdByRow, transaction: null, ct);
        if (changed.Count == 0) return 0;

        await audit.LogActivitiesAsync(
            AuditActions.Updated, AuditEntityTypes.Record, changed.Select(c => c.PublicId.ToString()).ToList(),
            $"Record linked to {parent.Name} through its unmatched {Label(target.Reference)} value", target.Child.AppId, transaction: null, ct);
        await PublishIndexUpdatesAsync(target, changed, ct);
        return changed.Count;
    }

    /// <summary>The parent a kept text belongs to: by key text first, then (for a number or date key) by value. Null when no
    /// record of the job has that key, or more than one does.</summary>
    private static long? Match(AppField key, string text, Dictionary<string, long?> byText, Dictionary<object, long?> byValue)
    {
        if (byText.TryGetValue(text, out var byKeyText)) return byKeyText;
        return byValue.Count > 0 && KeyFieldResolver.ConvertToColumnType(key, text) is { } typed && byValue.TryGetValue(typed, out var byKeyValue)
            ? byKeyValue
            : null;
    }

    /// <summary>Records <paramref name="id"/> under <paramref name="key"/>; a key a second record also has maps to null (ambiguous).</summary>
    private static void Add<TKey>(Dictionary<TKey, long?> map, TKey key, long id) where TKey : notnull
        => map[key] = map.TryGetValue(key, out var seen) && seen != id ? null : id;

    private async Task PublishIndexUpdatesAsync(Target target, IReadOnlyList<(Guid PublicId, long ParentId)> changed, CancellationToken ct)
    {
        var indexesReference = target.Reference.IsSearchable || target.Reference.IsFilterable;
        var indexesOriginal = target.Original.IsSearchable || target.Original.IsFilterable;
        if (!indexesReference && !indexesOriginal) return;

        var messages = changed.Select(c =>
        {
            var payload = new Dictionary<string, object?>();
            if (indexesReference) payload[target.Reference.Fid!.Value.ToString()] = c.ParentId;
            if (indexesOriginal) payload[target.Original.Fid!.Value.ToString()] = null;
            return new SearchIndexMessage
            {
                Action = IndexAction.Upsert,
                TenantId = queryContext.TenantId,
                AppId = target.Child.AppId,
                TableId = target.Child.Id,
                RecordPublicId = c.PublicId,
                Payload = payload,
            };
        }).ToList();
        // The links are already saved (separate transaction), so the change is recorded right after; the outbox then keeps the index
        // catching up from depending on this one publish.
        if (outbox != null) await outbox.AddAsync(messages, null, ct);
        // Like every other write path: the search index catching up is not a condition of the data change.
        _ = publisher.PublishBatchAsync(messages, default);
    }

    private static IReadOnlySet<long> KeyFids(IReadOnlyList<Target> targets) => targets.Select(t => (long)t.Key.Fid!.Value).ToHashSet();

    /// <summary>The relationships in which <paramref name="parent"/> is the parent and whose Reference keeps unmatched text, read once
    /// per scope (a request, or one job of the worker).</summary>
    private async Task<IReadOnlyList<Target>> TargetsAsync(AppTable parent, CancellationToken ct)
    {
        if (_targets.TryGetValue(parent.Id, out var known)) return known;

        var found = new List<Target>();
        var rels = await relationships.ListByParentTableAsync(parent.Id, ct);
        if (rels.Count > 0)
        {
            var parentFields = await fields.ListByTableAsync(parent.Id, ct);
            foreach (var rel in rels.Where(r => !r.IsDeleted))
            {
                // Record ID# (and any other system or computed key) is never a value a write sets, so nothing can come to match it.
                var key = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields);
                if (key?.Fid is null || key.IsSystem || PhysicalNaming.IsComputedTypeCode(key.TypeCode)) continue;

                var childFields = rel.ChildTableId == parent.Id ? parentFields : await fields.ListByTableAsync(rel.ChildTableId, ct);
                var reference = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId);
                if (FormulaTypeMap.ParseReferenceSettings(reference?.Settings)?.OriginalFid is not int originalFid) continue;
                var original = childFields.FirstOrDefault(f => f.Fid == originalFid && !f.IsDeleted);
                if (original is null) continue;

                var child = rel.ChildTableId == parent.Id ? parent : await tables.GetByIdAsync(rel.ChildTableId, ct);
                found.Add(new Target(child, reference!, original, key));
            }
        }
        return _targets[parent.Id] = found;
    }

    /// <summary>True when two values of a key are the same key: as text, ignoring case and surrounding spaces, or as numbers
    /// (10 and 10.0000) or dates.</summary>
    private static bool SameKey(object? before, object? after)
    {
        var a = KeyText(before);
        var b = KeyText(after);
        if (a is null || b is null) return a is null && b is null;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var x)
            && decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var y)) return x == y;
        return DateTime.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)
            && DateTime.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) && d1 == d2;
    }

    /// <summary>A key value as the culture-independent text it is matched by (the same form the relationship conversion stores in
    /// the "(original)" field); null when blank.</summary>
    private static string? KeyText(object? value)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = value switch
        {
            null => null,
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => je.GetRawText(),
            },
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", inv) : d.ToString("yyyy-MM-ddTHH:mm:ss", inv),
            DateOnly d => d.ToString("yyyy-MM-dd", inv),
            IFormattable f => f.ToString(null, inv),
            _ => value.ToString(),
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string Label(AppField field) => string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;
}
