using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>A dependent-dropdown condition resolved against live metadata and the submitted form
/// value, ready for the record repository to turn into SQL. Exactly one of <see cref="ParentField"/>
/// (direct) or <see cref="JunctionTable"/> (junction) is set. A blank <see cref="Value"/> means the
/// controlling field has no selection yet, so nothing matches. A clause with <see cref="Tree"/> set is
/// instead a whole filter tree over the parent table (its form-field comparisons already replaced by the
/// submitted values), judged against <see cref="TreeFields"/> — the parent table's fields.</summary>
public sealed record ReferenceFilterClause(
    AppField? ParentField,
    AppTable? JunctionTable,
    AppField? JunctionParentField,
    AppField? JunctionValueField,
    string? Value,
    FilterGroup? Tree = null,
    IReadOnlyList<AppField>? TreeFields = null);

/// <summary>Builds <see cref="ReferenceFilterClause"/>s from a Reference field's
/// <see cref="ReferenceSettings.FilterConditions"/> and the current values of the form's other
/// fields (keyed by Fid). Conditions that point at a since-deleted field/table, or whose
/// controlling field wasn't supplied (not on the form), are skipped rather than blocking the dropdown.
/// In the filter tree a Reference is compared, as in a summary's matching criteria, as the key its parent
/// shows (<see cref="ReferenceKeys"/>): "Related Division is Engineering", not its Record ID#.</summary>
public static class ReferenceFilterResolver
{
    /// <summary>The References a tree compares as their parent's key: each parent Reference the conditions read
    /// moved onto a stand-in lookup of its key (<see cref="ReferenceKeys.StandIn"/>), and the key value of the
    /// record each submitted form Reference points at.</summary>
    private sealed record TreeKeys(
        IReadOnlyDictionary<long, long> Moved,
        IReadOnlyDictionary<long, AppField> Sources,
        IReadOnlyList<AppField> TreeFields,
        IReadOnlyDictionary<int, string?> FormKeyValues);

    private static readonly JsonSerializerOptions TreeJsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The saved filter tree, or null when there is none (or it can't be read).</summary>
    public static FilterGroup? ParseTree(ReferenceSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(settings?.FilterTree)) return null;
        try
        {
            var tree = JsonSerializer.Deserialize<FilterGroup>(settings.FilterTree, TreeJsonOpts);
            return tree is { Nodes.Count: > 0 } ? tree : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Fids of the form fields whose current value this Reference's dropdown depends on:
    /// the legacy conditions' controlling fields plus every "parentField" comparison in the tree.</summary>
    public static IReadOnlyList<int> ControllingFids(ReferenceSettings? settings)
    {
        var fids = new List<int>();
        foreach (var c in settings?.FilterConditions ?? [])
            if (c.FormFid is int f) fids.Add(f);
        if (ParseTree(settings) is { } tree) CollectFormFids(tree, fids);
        return fids.Distinct().ToList();
    }

    /// <summary>True when the Reference has any filter at all (legacy conditions or a tree).</summary>
    public static bool HasFilter(ReferenceSettings? settings) =>
        settings?.FilterConditions is { Count: > 0 } || ParseTree(settings) is not null;

    private static void CollectFormFids(FilterGroup group, List<int> fids)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c && ParentFieldScope.IsParentFieldMode(c.ValueMode) && c.ValueFieldId is long id)
                fids.Add((int)id);
            if (node.Group is { } sub) CollectFormFids(sub, fids);
        }
    }

    /// <summary>Replaces each form-field comparison in the tree with the submitted value as a literal.
    /// A comparison whose form field was deleted, or isn't part of the submission, is dropped (nothing to
    /// filter by); one whose field is blank — "no selection yet" — can match nothing. Groups left empty go.
    /// With <paramref name="keys"/>, a condition on a parent Reference that shows a key is moved onto that key,
    /// and a form Reference is compared as the key of the record it points at — except a form Reference
    /// compared to a parent Reference of the same table, where both hold that table's Record ID#.</summary>
    private static FilterGroup ResolveTree(FilterGroup group, IReadOnlyList<AppField> childFields,
        IReadOnlyList<AppField> parentFields, IReadOnlyDictionary<int, string?> formValues,
        IReadOnlyDictionary<long, AppField> lookupSources, long currentUserId, TreeKeys? keys)
    {
        var nodes = new List<FilterNode>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                if (string.Equals(c.Operator, "isCurrentUser", StringComparison.OrdinalIgnoreCase))
                {
                    // Judged against whoever is picking/saving. With no known user (a pipeline write)
                    // there is nobody to compare to, so the condition is left out rather than matching nothing.
                    if (currentUserId > 0)
                        nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = c.FieldId, Operator = "eq", Value = currentUserId.ToString() } });
                    continue;
                }
                if (string.Equals(c.Operator, "isNotCurrentUser", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentUserId > 0)
                    {
                        var isList = (parentFields.FirstOrDefault(f => f.Fid == (int)c.FieldId) ?? childFields.FirstOrDefault(f => f.Fid == (int)c.FieldId))
                            ?.TypeCode is "MultiUser" or "Formula_ListUser";
                        nodes.Add(new FilterNode { Group = Reports.Queries.RunReport.RunReportQueryHandler.NotCurrentUserGroup(c.FieldId, isList, currentUserId) });
                    }
                    continue;
                }
                if (!ParentFieldScope.IsParentFieldMode(c.ValueMode))
                {
                    nodes.Add(keys is null ? node : new FilterNode { Condition = MoveOntoKeys(c, keys) });
                    continue;
                }
                if (c.ValueFieldId is not long formFid || childFields.FirstOrDefault(f => f.Fid == (int)formFid) is not { } formField) continue;
                if (!formValues.TryGetValue((int)formFid, out var raw)) continue;
                var compared = parentFields.FirstOrDefault(f => f.Fid == (int)c.FieldId);
                var fieldId = c.FieldId;
                if (keys is not null && !SameParentTable(compared, formField))
                {
                    if (keys.FormKeyValues.TryGetValue((int)formFid, out var keyValue)) raw = keyValue;
                    if (keys.Moved.TryGetValue(c.FieldId, out var standIn)) (fieldId, compared) = (standIn, keys.Sources[standIn]);
                }
                var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
                // A value the compared column can't hold (a date that isn't one, text for a number) would
                // fail in SQL; it can't match anything either, so treat it as no selection.
                // A Lookup column holds its source's type.
                if (compared is not null && lookupSources.TryGetValue(fieldId, out var lookupSource)) compared = lookupSource;
                if (value is not null && !FitsColumn(compared, value))
                    value = null;
                nodes.Add(new FilterNode
                {
                    Condition = value is null
                        // Record ID# is never negative: a condition that can't hold.
                        ? new FilterCondition { FieldId = 3, Operator = "eq", Value = "-1" }
                        : new FilterCondition { FieldId = fieldId, Operator = c.Operator, Value = value, SubField = c.SubField, ValueMode = "literal" },
                });
            }
            else if (node.Group is { } sub)
            {
                var resolved = ResolveTree(sub, childFields, parentFields, formValues, lookupSources, currentUserId, keys);
                if (resolved.Nodes.Count > 0) nodes.Add(new FilterNode { Group = resolved });
            }
        }
        return new FilterGroup { Logic = group.Logic, Nodes = nodes };
    }

    /// <summary>A copy of <paramref name="c"/> on the stand-in key of a parent Reference it filters on or — "the value
    /// in the field" — compares to.</summary>
    private static FilterCondition MoveOntoKeys(FilterCondition c, TreeKeys keys) => new()
    {
        FieldId = keys.Moved.GetValueOrDefault(c.FieldId, c.FieldId),
        Operator = c.Operator,
        Value = c.Value,
        SubField = c.SubField,
        ValueMode = c.ValueMode,
        ValueFieldId = string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && c.ValueFieldId is long other
            ? keys.Moved.GetValueOrDefault(other, other)
            : c.ValueFieldId,
    };

    /// <summary>True when both are References to the same table — each holds a Record ID# of it.</summary>
    private static bool SameParentTable(AppField? a, AppField b) =>
        a is { TypeCode: "Reference" } && b.TypeCode == "Reference"
        && FormulaTypeMap.ParseReferenceSettings(a.Settings)?.ParentTableId is long tableId
        && FormulaTypeMap.ParseReferenceSettings(b.Settings)?.ParentTableId == tableId;

    /// <summary>The keys the tree's References are compared as (see <see cref="TreeKeys"/>), or null when it reads
    /// no Reference that shows a key.</summary>
    private static async Task<TreeKeys?> LoadKeysAsync(
        FilterGroup tree, ReferenceSettings? settings, IReadOnlyList<AppField> childFields, IReadOnlyList<AppField> parentFields,
        IReadOnlyDictionary<int, string?> formValues, IRelationshipRepository relRepo, IRecordRepository? recordRepo,
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        var parentKeys = settings?.ParentTableId is long parentTableId
            && ReferenceKeys.AnyReference(parentFields, SummaryEncryptionGuard.ConditionFieldIds(tree))
            ? await ReferenceKeys.LoadAsync(parentTableId, parentFields, relRepo, tableRepo, fieldRepo, ct)
            : null;
        var parentFieldsByFid = parentFields.Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var standIn = ReferenceKeys.StandIn(tree, null, parentFieldsByFid, parentKeys);

        // A form Reference holds the Record ID# of the record picked in it: compared as that record's key.
        var formKeyValues = new Dictionary<int, string?>();
        var formFids = new List<int>();
        CollectFormFids(tree, formFids);
        var childTableId = childFields.Select(f => f.AppTableId).FirstOrDefault(id => id > 0);
        if (recordRepo is not null && childTableId > 0 && ReferenceKeys.AnyReference(childFields, formFids.Select(f => (long)f)))
        {
            var childKeys = await ReferenceKeys.LoadAsync(childTableId, childFields, relRepo, tableRepo, fieldRepo, ct);
            foreach (var fid in formFids.Distinct())
            {
                if (!childKeys.TryGetValue(fid, out var key) || !formValues.TryGetValue(fid, out var raw)) continue;
                var reference = childFields.First(f => f.Fid == fid);
                formKeyValues[fid] = await KeyValueAsync(reference, key, raw, recordRepo, tableRepo, ct);
            }
        }

        if (standIn is null && formKeyValues.Count == 0) return null;
        return new TreeKeys(
            standIn?.Moved ?? new Dictionary<long, long>(),
            standIn?.Sources ?? new Dictionary<long, AppField>(),
            standIn is null ? parentFields : [.. parentFields, .. standIn.Value.Lookups],
            formKeyValues);
    }

    /// <summary>The key value of the record <paramref name="raw"/> (a Record ID#) picked in the form's
    /// <paramref name="reference"/> — null when nothing is picked or the record is gone, so nothing matches.</summary>
    private static async Task<string?> KeyValueAsync(AppField reference, AppField key, string? raw,
        IRecordRepository recordRepo, IAppTableRepository tableRepo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw) || !long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowId))
            return null;
        AppTable table;
        try { table = await tableRepo.GetByIdAsync(FormulaTypeMap.ParseReferenceSettings(reference.Settings)?.ParentTableId ?? key.AppTableId, ct); }
        catch (Domain.Exceptions.NotFoundException) { return null; }
        var rows = await recordRepo.GetRowsByIdsAsync(table, [key], [rowId], ct);
        return rows.TryGetValue(rowId, out var row) && row.TryGetValue(EncryptedRowFilter.Column(key), out var value)
            ? InvariantText(value)
            : null;
    }

    private static string? InvariantText(object? value) => value switch
    {
        null or DBNull => null,
        DateTime d => d.TimeOfDay == TimeSpan.Zero
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Whether <paramref name="value"/> can be compared with <paramref name="field"/>'s column: a date
    /// column needs a date, a numeric one a number. Other columns take any text.</summary>
    private static bool FitsColumn(AppField? field, string value) => field?.TypeCode switch
    {
        "Date" or "DateTime" => DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _),
        "Number" or "Currency" or "Percent" or "Rating" or "Duration" => decimal.TryParse(value,
            System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _),
        _ => true,
    };

    public static async Task<IReadOnlyList<ReferenceFilterClause>> BuildAsync(
        ReferenceSettings? settings,
        IReadOnlyList<AppField> childFields,
        IReadOnlyList<AppField> parentFields,
        IReadOnlyDictionary<int, string?> formValues,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        CancellationToken ct,
        long currentUserId = 0,
        IRelationshipRepository? relRepo = null,
        IRecordRepository? recordRepo = null)
    {
        var clauses = new List<ReferenceFilterClause>();

        if (ParseTree(settings) is { } tree)
        {
            var lookupSources = await SummaryLookupSources.LoadAsync(parentFields, fieldRepo, ct);
            // Without the relationships the keys can't be known: References are then compared as their Record ID#.
            var keys = relRepo is null ? null
                : await LoadKeysAsync(tree, settings, childFields, parentFields, formValues, relRepo, recordRepo, tableRepo, fieldRepo, ct);
            var resolved = ResolveTree(tree, childFields, parentFields, formValues, lookupSources, currentUserId, keys);
            if (resolved.Nodes.Count > 0)
                clauses.Add(new ReferenceFilterClause(null, null, null, null, null, resolved, keys?.TreeFields ?? parentFields));
        }

        if (settings?.FilterConditions is not { Count: > 0 } conditions) return clauses;

        foreach (var c in conditions)
        {
            if (c.FormFid is not int formFid) continue;

            // The controlling field was deleted since this was configured: an unfilterable
            // condition must not leave the dropdown permanently empty, so drop it.
            if (!childFields.Any(f => f.Fid == formFid)) continue;

            // The caller didn't supply the controlling field at all (it isn't on this form): there
            // is nothing to filter by, so don't filter. A supplied-but-blank value is different —
            // "no selection yet" — and matches nothing.
            if (!formValues.TryGetValue(formFid, out var raw)) continue;
            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

            if (c.JunctionTableId is long junctionTableId)
            {
                AppTable junction;
                try { junction = await tableRepo.GetByIdAsync(junctionTableId, ct); }
                catch (Domain.Exceptions.NotFoundException) { continue; }

                var jFields = await fieldRepo.ListByTableAsync(junction.Id, ct);
                var jParent = jFields.FirstOrDefault(f => f.Fid == c.JunctionParentFid);
                var jValue = jFields.FirstOrDefault(f => f.Fid == c.JunctionValueFid);
                if (jParent is null || jValue is null) continue;
                clauses.Add(new ReferenceFilterClause(null, junction, jParent, jValue, value));
            }
            else
            {
                var parentField = parentFields.FirstOrDefault(f => f.Fid == c.ParentFid);
                if (parentField is null) continue;
                clauses.Add(new ReferenceFilterClause(parentField, null, null, null, value));
            }
        }

        return clauses;
    }
}
