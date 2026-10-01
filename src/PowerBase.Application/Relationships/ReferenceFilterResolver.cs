using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>A dependent-dropdown condition resolved against live metadata and the submitted form
/// value, ready for the record repository to turn into SQL. Exactly one of <see cref="ParentField"/>
/// (direct) or <see cref="JunctionTable"/> (junction) is set. A blank <see cref="Value"/> means the
/// controlling field has no selection yet, so nothing matches.</summary>
public sealed record ReferenceFilterClause(
    AppField? ParentField,
    AppTable? JunctionTable,
    AppField? JunctionParentField,
    AppField? JunctionValueField,
    string? Value);

/// <summary>Builds <see cref="ReferenceFilterClause"/>s from a Reference field's
/// <see cref="ReferenceSettings.FilterConditions"/> and the current values of the form's other
/// fields (keyed by Fid). Conditions that point at a since-deleted field/table, or whose
/// controlling field wasn't supplied (not on the form), are skipped rather than blocking the dropdown.</summary>
public static class ReferenceFilterResolver
{
    public static async Task<IReadOnlyList<ReferenceFilterClause>> BuildAsync(
        ReferenceSettings? settings,
        IReadOnlyList<AppField> childFields,
        IReadOnlyList<AppField> parentFields,
        IReadOnlyDictionary<int, string?> formValues,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        CancellationToken ct)
    {
        var clauses = new List<ReferenceFilterClause>();
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
