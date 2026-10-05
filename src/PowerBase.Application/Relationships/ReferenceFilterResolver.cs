using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
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
/// controlling field wasn't supplied (not on the form), are skipped rather than blocking the dropdown.</summary>
public static class ReferenceFilterResolver
{
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
    /// filter by); one whose field is blank — "no selection yet" — can match nothing. Groups left empty go.</summary>
    private static FilterGroup ResolveTree(FilterGroup group, IReadOnlyList<AppField> childFields,
        IReadOnlyList<AppField> parentFields, IReadOnlyDictionary<int, string?> formValues,
        IReadOnlyDictionary<long, AppField> lookupSources, long currentUserId)
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
                if (!ParentFieldScope.IsParentFieldMode(c.ValueMode)) { nodes.Add(node); continue; }
                if (c.ValueFieldId is not long formFid || !childFields.Any(f => f.Fid == (int)formFid)) continue;
                if (!formValues.TryGetValue((int)formFid, out var raw)) continue;
                var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
                // A value the compared column can't hold (a date that isn't one, text for a number) would
                // fail in SQL; it can't match anything either, so treat it as no selection.
                // A Lookup column holds its source's type.
                var compared = parentFields.FirstOrDefault(f => f.Fid == (int)c.FieldId);
                if (compared is not null && lookupSources.TryGetValue(c.FieldId, out var lookupSource)) compared = lookupSource;
                if (value is not null && !FitsColumn(compared, value))
                    value = null;
                nodes.Add(new FilterNode
                {
                    Condition = value is null
                        // Record ID# is never negative: a condition that can't hold.
                        ? new FilterCondition { FieldId = 3, Operator = "eq", Value = "-1" }
                        : new FilterCondition { FieldId = c.FieldId, Operator = c.Operator, Value = value, SubField = c.SubField, ValueMode = "literal" },
                });
            }
            else if (node.Group is { } sub)
            {
                var resolved = ResolveTree(sub, childFields, parentFields, formValues, lookupSources, currentUserId);
                if (resolved.Nodes.Count > 0) nodes.Add(new FilterNode { Group = resolved });
            }
        }
        return new FilterGroup { Logic = group.Logic, Nodes = nodes };
    }

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
        long currentUserId = 0)
    {
        var clauses = new List<ReferenceFilterClause>();

        if (ParseTree(settings) is { } tree)
        {
            var lookupSources = await SummaryLookupSources.LoadAsync(parentFields, fieldRepo, ct);
            var resolved = ResolveTree(tree, childFields, parentFields, formValues, lookupSources, currentUserId);
            if (resolved.Nodes.Count > 0)
                clauses.Add(new ReferenceFilterClause(null, null, null, null, null, resolved, parentFields));
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
