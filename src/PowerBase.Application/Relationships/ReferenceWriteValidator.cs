using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

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
        IRelationshipRepository relRepo,
        CancellationToken ct)
    {
        var overrides = new Dictionary<long, object?>();

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
                continue;

            // 1b. A parent record PublicId (Guid) — resolve to its internal row Id.
            if (Guid.TryParse(s, out var parentPublicId))
            {
                var rowId = await recordRepo.GetRecordIdByPublicIdAsync(parent, parentPublicId, ct: ct);
                if (rowId > 0)
                {
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
}
