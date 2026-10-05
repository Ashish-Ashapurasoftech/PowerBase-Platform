using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Resolves and validates submitted Reference field values. The reference column always stores
/// the parent's internal row Id, so:
///   • a value that is already a live parent row Id is kept as-is (the reference picker submits
///     exactly this — see <see cref="Queries.GetParentOptionsQueryHandler"/>);
///   • any other value is treated as a human key and translated to the row Id via the
///     relationship's display key column (per-relationship <c>DisplayKeyFieldId</c> override →
///     parent table <c>KeyFieldId</c>) — this is the path API / import / pipeline / formula
///     writes take when they reference a record by its readable key.
/// Returns a map of Fid → resolved row Id for every value that needed translation; callers
/// overlay it on the submitted values before persisting.
/// </summary>
public static class ReferenceWriteValidator
{
    public static async Task<Dictionary<long, object?>> ValidateAsync(
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> values,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRelationshipRepository? relRepo,
        CancellationToken ct,
        IReadOnlyDictionary<long, object?>? existingValues = null)
    {
        var overrides = new Dictionary<long, object?>();

        // Dependent dropdown: reject a value the picker would not have offered.
        //  • Only conditions whose controlling field is part of this submission are judged (a
        //    controlling field that isn't supplied can't be checked, and must not block the write).
        //  • On an update (existingValues supplied), only when the Reference or one of its controlling
        //    fields actually changed — otherwise saving an unrelated edit of a record whose current
        //    value predates the rule would start failing.
        async Task EnforceFilterAsync(AppField field, ReferenceSettings settings, AppTable parent, long parentRowId)
        {
            if (!ReferenceFilterResolver.HasFilter(settings)) return;
            // The controlling value is the submitted one, or — on an update that doesn't resend it —
            // the record's current stored one (a partial update of just the Reference must still be
            // judged against the record's own controlling value).
            bool TryControlling(int fid, out object? value)
            {
                if (values.TryGetValue(fid, out value)) return true;
                if (existingValues is not null && existingValues.TryGetValue(fid, out value)) return true;
                value = null;
                return false;
            }

            // A filter on literal values alone (no controlling field) is judged on every write.
            var controllingFids = ReferenceFilterResolver.ControllingFids(settings)
                .Where(fid => fields.Any(f => f.Fid == fid) && TryControlling(fid, out _))
                .ToList();

            if (existingValues is not null)
            {
                var referenceChanged = !SameValue(existingValues.GetValueOrDefault(field.Fid!.Value), parentRowId);
                var controllingChanged = controllingFids.Any(fid =>
                    values.TryGetValue(fid, out var submitted)
                    && !SameValue(existingValues.GetValueOrDefault(fid), submitted));
                if (!referenceChanged && !controllingChanged) return;
            }

            var formValues = controllingFids.ToDictionary(
                fid => fid,
                fid => { TryControlling(fid, out var v); return InvariantText(v); });
            var parentFields = await fieldRepo.ListByTableAsync(parent.Id, ct);
            var clauses = await ReferenceFilterResolver.BuildAsync(settings, fields, parentFields, formValues, tableRepo, fieldRepo, ct);
            if (clauses.Count == 0) return;   // nothing applicable to this submission
            if (!await recordRepo.MatchesReferenceFilterAsync(parent, parentRowId, clauses, ct))
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    [field.Name] = [$"The selected {parent.Name} record is not allowed for the current selection."],
                });
        }

        foreach (var field in fields.Where(f => f.TypeCode == "Reference" && f.Fid.HasValue))
        {
            if (!values.TryGetValue(field.Fid!.Value, out var raw) || raw is null) continue;
            var s = raw.ToString();
            if (string.IsNullOrWhiteSpace(s)) continue;

            var settings = FormulaTypeMap.ParseReferenceSettings(field.Settings);
            if (settings?.ParentTableId is not long parentTableId) continue;

            var parent = await tableRepo.GetByIdAsync(parentTableId, ct);

            // 1. Already a row Id pointing at a live parent record — the common (picker) path.
            if (long.TryParse(s, out var parentRowId) && await recordRepo.ExistsAsync(parent, parentRowId, ct))
            {
                await EnforceFilterAsync(field, settings, parent, parentRowId);
                continue;
            }

            // 1b. A parent record PublicId (Guid) — resolve to its internal row Id.
            if (Guid.TryParse(s, out var parentPublicId))
            {
                var rowId = await recordRepo.GetRecordIdByPublicIdAsync(parent, parentPublicId, ct: ct);
                if (rowId > 0)
                {
                    await EnforceFilterAsync(field, settings, parent, rowId);
                    overrides[field.Fid!.Value] = rowId;
                    continue;
                }
            }

            // 2. Treat the value as a human key and resolve it through the display key column.
            Relationship? rel = settings.RelationshipId is long relId && relRepo is not null
                ? await relRepo.GetByIdAsync(relId, ct)
                : null;
            var parentFields = await fieldRepo.ListByTableAsync(parent.Id, ct);
            var displayKey = KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields);
            if (displayKey is not null)
            {
                var typedValue = KeyFieldResolver.ConvertToColumnType(displayKey, s);
                if (typedValue is not null)
                {
                    var col = KeyFieldResolver.ColumnName(displayKey);
                    var matches = await recordRepo.GetIdsByColumnValuesAsync(parent, col, [typedValue], ct);
                    if (matches.Count > 0)
                    {
                        await EnforceFilterAsync(field, settings, parent, matches.Values.First());
                        overrides[field.Fid!.Value] = matches.Values.First();
                        continue;
                    }
                }
            }

            // 3. Neither a live row Id nor a resolvable key.
            throw new ValidationException(
                new Dictionary<string, string[]> { [field.Name] = [$"The referenced {parent.Name} record does not exist."] });
        }

        return overrides;
    }

    /// <summary>A submitted or stored value as the culture-independent text the filter resolver reads: a date as
    /// ISO (stored values arrive as DateTime), a number with a '.' decimal point — plain ToString() would follow
    /// the server's culture and a date or decimal could then be misread.</summary>
    private static string? InvariantText(object? value)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return value switch
        {
            null => null,
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", inv) : d.ToString("yyyy-MM-ddTHH:mm:ss", inv),
            DateOnly d => d.ToString("yyyy-MM-dd", inv),
            IFormattable f => f.ToString(null, inv),
            _ => value.ToString(),
        };
    }

    /// <summary>Loose equality between a stored value and a submitted one: null and blank are the same,
    /// numbers compare numerically (a DECIMAL column reads back "5.0000" for a submitted "5"), and
    /// everything else compares as case-insensitive trimmed text.</summary>
    private static bool SameValue(object? a, object? b)
    {
        var sa = a?.ToString()?.Trim();
        var sb = b?.ToString()?.Trim();
        if (string.IsNullOrEmpty(sa) && string.IsNullOrEmpty(sb)) return true;
        if (string.IsNullOrEmpty(sa) || string.IsNullOrEmpty(sb)) return false;
        if (decimal.TryParse(sa, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var da)
            && decimal.TryParse(sb, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var db))
            return da == db;
        return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }
}
