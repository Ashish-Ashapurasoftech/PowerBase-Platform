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
        IReadOnlyDictionary<long, object?>? existingValues = null,
        ReferenceFilterUsers? users = null)
    {
        var overrides = new Dictionary<long, object?>();

        // A Lookup that a dropdown depends on ("Related Project Type") has no column: its value is worked out below
        // from the Reference it pulls through, as that Reference stands after this write. Filled before any check runs.
        var lookupValues = new Dictionary<int, object?>();

        // The Reference a Lookup pulls through, when that Lookup is one of these fields.
        int? LookupReferenceFid(int fid) =>
            fields.FirstOrDefault(f => f.Fid == fid) is { TypeCode: "Lookup" } lookup
                ? FormulaTypeMap.ParseLookupSettings(lookup.Settings)?.ReferenceFid
                : null;

        // True when this update sends a Reference whose parent differs from the stored one.
        bool ReferenceChanged(int refFid) =>
            existingValues is not null && values.TryGetValue(refFid, out var sent)
            && !SameValue(existingValues.GetValueOrDefault(refFid), overrides.TryGetValue(refFid, out var resolved) ? resolved : sent);

        // Dependent dropdown: reject a value the picker would not have offered.
        //  • Only conditions whose controlling field is part of this submission are judged (a
        //    controlling field that isn't supplied can't be checked, and must not block the write).
        //  • On an update (existingValues supplied), only when the Reference or one of its controlling
        //    fields actually changed — otherwise saving an unrelated edit of a record whose current
        //    value predates the rule would start failing.
        async Task EnforceFilterAsync(AppField field, ReferenceSettings settings, AppTable parent, long parentRowId, bool storedValue = false)
        {
            if (!ReferenceFilterResolver.HasFilter(settings)) return;
            // The controlling value is the submitted one, or — on an update that doesn't resend it —
            // the record's current stored one (a partial update of just the Reference must still be
            // judged against the record's own controlling value).
            bool TryControlling(int fid, out object? value)
            {
                // A Lookup is judged on the value it takes from its Reference — never on a value the client sent for it
                // (a form may send the lookup's old value, or the parent's label).
                if (LookupReferenceFid(fid) is not null) return lookupValues.TryGetValue(fid, out value);
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
                var controllingChanged = controllingFids.Any(fid => LookupReferenceFid(fid) is int lookupRefFid
                    // A Lookup changes with the Reference it pulls through (Project QA → Launch).
                    ? ReferenceChanged(lookupRefFid)
                    : values.TryGetValue(fid, out var submitted) && !SameValue(existingValues.GetValueOrDefault(fid), submitted));
                if (!referenceChanged && !controllingChanged) return;
            }

            var formValues = controllingFids.ToDictionary(
                fid => fid,
                fid => { TryControlling(fid, out var v); return InvariantText(v); });
            var parentFields = await fieldRepo.ListByTableAsync(parent.Id, ct);
            var clauses = await ReferenceFilterResolver.BuildAsync(settings, fields, parentFields, formValues, tableRepo, fieldRepo, ct,
                relRepo: relRepo, recordRepo: recordRepo, users: users);
            if (clauses.Count == 0) return;   // nothing applicable to this submission
            if (!await recordRepo.MatchesReferenceFilterAsync(parent, parentRowId, clauses, ct))
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    [field.Name] = [storedValue
                        ? $"{field.Label ?? field.Name} no longer matches the new selection. Choose another {parent.Name} record or clear it."
                        : $"The selected {parent.Name} record is not allowed for the current selection."],
                });
        }

        // The dropdown checks run once every submitted Reference is resolved to its row Id, so a Lookup that pulls
        // through one of them can be worked out first.
        var checks = new List<(AppField Field, ReferenceSettings Settings, AppTable Parent, long RowId, bool StoredValue)>();

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
                checks.Add((field, settings, parent, parentRowId, false));
                continue;
            }

            // 1b. A parent record PublicId (Guid) — resolve to its internal row Id.
            if (Guid.TryParse(s, out var parentPublicId))
            {
                var rowId = await recordRepo.GetRecordIdByPublicIdAsync(parent, parentPublicId, ct: ct);
                if (rowId > 0)
                {
                    checks.Add((field, settings, parent, rowId, false));
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
                        checks.Add((field, settings, parent, matches.Values.First(), false));
                        overrides[field.Fid!.Value] = matches.Values.First();
                        continue;
                    }
                }
            }

            // 3. Neither a live row Id nor a resolvable key.
            throw new ValidationException(
                new Dictionary<string, string[]> { [field.Name] = [$"The referenced {parent.Name} record does not exist."] });
        }

        // A Reference left out of an update keeps its stored parent. When one of its controlling fields changes in
        // this update, that stored parent must still be allowed: Grid Edit and the API send only the changed cells,
        // so changing Task Type alone would otherwise keep an Activity that belongs to another type. A Reference
        // that is sent (picked, or cleared) was judged above.
        if (existingValues is not null)
        {
            foreach (var field in fields.Where(f => f.TypeCode == "Reference" && f.Fid.HasValue && !values.ContainsKey(f.Fid!.Value)))
            {
                if (StoredRowId(existingValues.GetValueOrDefault(field.Fid!.Value)) is not long storedRowId) continue;
                var settings = FormulaTypeMap.ParseReferenceSettings(field.Settings);
                if (settings?.ParentTableId is not long parentTableId || !ReferenceFilterResolver.HasFilter(settings)) continue;
                // A controlling field sent in this update — or, for a Lookup, the Reference it pulls through.
                if (!ReferenceFilterResolver.ControllingFids(settings).Any(fid =>
                        LookupReferenceFid(fid) is int lookupRefFid ? values.ContainsKey(lookupRefFid) : values.ContainsKey(fid)))
                    continue;
                var parent = await tableRepo.GetByIdAsync(parentTableId, ct);
                // A stored parent that no longer exists can't be judged; leave it to the delete guards.
                if (!await recordRepo.ExistsAsync(parent, storedRowId, ct)) continue;
                checks.Add((field, settings, parent, storedRowId, true));
            }
        }

        if (checks.Count == 0) return overrides;

        // Work out each Lookup the checks depend on, from its Reference as it stands after this write.
        foreach (var fid in checks.SelectMany(c => ReferenceFilterResolver.ControllingFids(c.Settings)).Distinct())
        {
            if (fields.FirstOrDefault(f => f.Fid == fid) is not { TypeCode: "Lookup" } lookup) continue;
            if (await LookupValueAsync(lookup, fields, values, existingValues, overrides, tableRepo, fieldRepo, recordRepo, ct) is (true, var value))
                lookupValues[fid] = value;
        }

        foreach (var c in checks)
            await EnforceFilterAsync(c.Field, c.Settings, c.Parent, c.RowId, c.StoredValue);

        return overrides;
    }

    /// <summary>The value a Lookup takes for this write: its source field on the parent record that its Reference points at
    /// once the write is applied (the submitted parent, or the stored one when the Reference isn't sent). A blank Reference
    /// gives a blank value. Known is false when it can't be worked out here — the Reference is neither sent nor stored, or
    /// the source has no column of its own (a Formula, Summary or another Lookup) — and the condition is then left out, as
    /// for any controlling field that isn't part of the write.</summary>
    private static async Task<(bool Known, object? Value)> LookupValueAsync(
        AppField lookup, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values,
        IReadOnlyDictionary<long, object?>? existingValues, IReadOnlyDictionary<long, object?> overrides,
        IAppTableRepository tableRepo, IAppFieldRepository fieldRepo, IRecordRepository recordRepo, CancellationToken ct)
    {
        var settings = FormulaTypeMap.ParseLookupSettings(lookup.Settings);
        if (settings?.ReferenceFid is not int refFid || settings.SourceFid is not int sourceFid) return (false, null);
        var reference = fields.FirstOrDefault(f => f.Fid == refFid && f.TypeCode == "Reference");
        var parentTableId = FormulaTypeMap.ParseReferenceSettings(reference?.Settings)?.ParentTableId ?? settings.SourceTableId;
        if (parentTableId is not long tableId) return (false, null);

        object? raw;
        if (overrides.TryGetValue(refFid, out var resolved)) raw = resolved;
        else if (values.TryGetValue(refFid, out var sent)) raw = sent;
        else if (existingValues is not null && existingValues.TryGetValue(refFid, out var stored)) raw = stored;
        else return (false, null);
        if (StoredRowId(raw) is not long parentRowId) return (true, null);

        AppTable parent;
        try { parent = await tableRepo.GetByIdAsync(tableId, ct); }
        catch (NotFoundException) { return (false, null); }
        var source = (await fieldRepo.ListByTableAsync(parent.Id, ct)).FirstOrDefault(f => f.Fid == sourceFid);
        if (source is null || Domain.Constants.PhysicalNaming.IsComputedTypeCode(source.TypeCode)) return (false, null);

        var rows = await recordRepo.GetRowsByIdsAsync(parent, [source], [parentRowId], ct);
        if (!rows.TryGetValue(parentRowId, out var row) || !row.TryGetValue(EncryptedRowFilter.Column(source), out var value)
            || value is DBNull)
            return (true, null);
        return (true, string.IsNullOrWhiteSpace(settings.SourceSubField) ? value : RelationalProjector.ExtractJsonSubField(value, settings.SourceSubField));
    }

    /// <summary>A stored Reference value as the parent row Id it holds (a BIGINT column may read back as long or
    /// decimal), or null when it is blank or not a positive whole number.</summary>
    private static long? StoredRowId(object? stored)
    {
        var text = stored?.ToString()?.Trim();
        return decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d)
            && d > 0 && d == decimal.Truncate(d) ? (long)d : null;
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
