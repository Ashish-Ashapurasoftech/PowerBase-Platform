using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// A Reference is compared in a summary's matching criteria — and summarized — as the value it shows: its parent's
/// display key (the relationship's own override, else the parent table's Set Key — see
/// <see cref="KeyFieldResolver.ResolveDisplayKey"/>), not the Record ID# its column holds; the value a lookup of that key
/// reads. So "Related Client" (Clients keyed by Client Name) is compared as Text, and Combined Text joins client names,
/// as in Quickbase. A Reference whose parent has no key set shows the Record ID# its column holds, and stays that number.
/// The column always stores the parent's Record ID# — only how a summary reads it changes.
/// </summary>
public static class ReferenceKeys
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const int RecordIdFid = 3;

    /// <summary>The display key each Reference among <paramref name="fields"/> (all of table <paramref name="tableId"/>)
    /// shows, keyed by the Reference's Fid — only a stored key field; a Reference showing Record ID# is absent. One field
    /// list per parent table, and none at all when there are no References.</summary>
    public static async Task<IReadOnlyDictionary<long, AppField>> LoadAsync(
        long tableId, IReadOnlyList<AppField> fields,
        IRelationshipRepository relRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        var result = new Dictionary<long, AppField>();
        var references = fields.Where(f => f.Fid.HasValue && f.TypeCode == nameof(FieldTypeCode.Reference)).ToList();
        if (references.Count == 0) return result;

        var relationships = await relRepo.ListByChildTableAsync(tableId, ct);
        var parents = new Dictionary<long, (AppTable Table, IReadOnlyList<AppField> Fields)>();
        foreach (var reference in references)
        {
            var settings = FormulaTypeMap.ParseReferenceSettings(reference.Settings);
            var rel = relationships.FirstOrDefault(r => settings?.RelationshipId is long relId ? r.Id == relId : r.ReferenceFid == reference.Fid);
            if ((settings?.ParentTableId ?? rel?.ParentTableId) is not long parentTableId) continue;
            if (!parents.TryGetValue(parentTableId, out var parent))
            {
                parent = (await tableRepo.GetByIdAsync(parentTableId, ct), await fieldRepo.ListByTableAsync(parentTableId, ct));
                parents[parentTableId] = parent;
            }
            // A calculated key has no column to read — the Reference stays its Record ID#.
            if (KeyFieldResolver.ResolveDisplayKey(rel, parent.Table, parent.Fields) is { Fid: int keyFid } key
                && keyFid != RecordIdFid && !PhysicalNaming.IsComputedTypeCode(key.TypeCode))
                result[reference.Fid!.Value] = key;
        }
        return result;
    }

    /// <summary>True when any of <paramref name="fids"/> is a Reference among <paramref name="fields"/> — whether a summary
    /// needs the keys (<see cref="LoadAsync"/>) at all.</summary>
    public static bool AnyReference(IReadOnlyList<AppField> fields, IEnumerable<long> fids)
    {
        var references = fields.Where(f => f.Fid.HasValue && f.TypeCode == nameof(FieldTypeCode.Reference))
            .Select(f => (long)f.Fid!.Value).ToHashSet();
        return references.Count > 0 && fids.Any(references.Contains);
    }

    /// <summary>The type of the key <paramref name="field"/> shows (its display key in <paramref name="referenceKeys"/>,
    /// see <see cref="LoadAsync"/>: "Text" for "Related Client" keyed by Client Name) — what a summary judges and reads it
    /// as. Null for any other field, or a Reference showing Record ID#.</summary>
    public static string? KeyTypeCode(AppField field, IReadOnlyDictionary<long, AppField>? referenceKeys) =>
        field.TypeCode == nameof(FieldTypeCode.Reference) && field.Fid is int fid && referenceKeys?.GetValueOrDefault(fid) is { } key
            ? key.TypeCode
            : null;

    /// <summary>True for a Reference compared as its parent's key: one whose parent shows a key field.</summary>
    public static bool ComparesAsKey(AppField field, IReadOnlyDictionary<long, AppField>? referenceKeys) =>
        KeyTypeCode(field, referenceKeys) is not null;

    /// <summary>The Fid a Reference's key lookup stands in under — never a real field's (those are positive, and -1 is the
    /// public id), so the Reference's own column (which also ties each child to its parent) is left as it is.</summary>
    public static int StandInFid(int referenceFid) => -(1_000_000 + referenceFid);

    /// <summary>The criteria with each condition on — or "the value in the field" comparison to — a Reference that shows a
    /// key (<paramref name="referenceKeys"/>) moved onto a stand-in lookup of that key, with those lookups and the key
    /// fields they read (to add to the child's fields and lookup sources), and which Fid each Reference moved to — the
    /// summary's target too (<paramref name="targetFid"/>), when it is one. Null when nothing reads such a Reference.</summary>
    public static (FilterGroup? Filter, IReadOnlyList<AppField> Lookups, IReadOnlyDictionary<long, AppField> Sources, IReadOnlyDictionary<long, long> Moved)? StandIn(
        FilterGroup? filter, int? targetFid, IReadOnlyDictionary<long, AppField> childFieldsByFid,
        IReadOnlyDictionary<long, AppField>? referenceKeys)
    {
        if (referenceKeys is not { Count: > 0 }) return null;
        var referenceFids = SummaryEncryptionGuard.ConditionFieldIds(filter)
            .Concat(targetFid is int t ? [t] : [])
            .Distinct()
            .Where(fid => childFieldsByFid.TryGetValue(fid, out var f) && ComparesAsKey(f, referenceKeys))
            .ToList();
        if (referenceFids.Count == 0) return null;

        var lookups = new List<AppField>();
        var sources = new Dictionary<long, AppField>();
        var moved = new Dictionary<long, long>();
        foreach (var fid in referenceFids)
        {
            var reference = childFieldsByFid[fid];
            var referenceSettings = FormulaTypeMap.ParseReferenceSettings(reference.Settings);
            var key = referenceKeys[fid];
            var standIn = StandInFid((int)fid);
            lookups.Add(new AppField
            {
                Id = standIn, Fid = standIn, AppTableId = reference.AppTableId, Name = reference.Name, Label = reference.Label,
                TypeCode = nameof(FieldTypeCode.Lookup),
                Settings = RelationshipFieldFactory.Serialize(new LookupSettings
                {
                    RelationshipId = referenceSettings?.RelationshipId,
                    ReferenceFid = (int)fid, SourceTableId = referenceSettings?.ParentTableId ?? key.AppTableId,
                    SourceFid = key.Fid, SourceTypeCode = key.TypeCode,
                }),
            });
            sources[standIn] = key;
            moved[fid] = standIn;
        }

        // A copy, so the saved criteria are never changed.
        FilterGroup? copy = null;
        if (filter is not null)
        {
            copy = JsonSerializer.Deserialize<FilterGroup>(JsonSerializer.Serialize(filter, JsonOpts), JsonOpts)!;
            Move(copy, moved);
        }
        return (copy, lookups, sources, moved);
    }

    private static void Move(FilterGroup group, IReadOnlyDictionary<long, long> moved)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                if (moved.TryGetValue(c.FieldId, out var to)) c.FieldId = to;
                if (string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase)
                    && c.ValueFieldId is long other && moved.TryGetValue(other, out var otherTo))
                    c.ValueFieldId = otherTo;
            }
            if (node.Group is { } sub) Move(sub, moved);
        }
    }
}
