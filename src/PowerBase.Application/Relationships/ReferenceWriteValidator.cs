using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>Validates that submitted Reference field values point at existing parent records.</summary>
public static class ReferenceWriteValidator
{
    public static async Task<Dictionary<long, object?>> ValidateAsync(
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> values,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
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
            var keyField = await KeyFieldResolver.ResolveAsync(parent, fieldRepo, ct);

            bool exists;
            if (keyField is null)
            {
                // Default key (Record ID#): the reference column stores the parent row Id, as before.
                if (!long.TryParse(s, out var parentId))
                    throw new ValidationException(new Dictionary<string, string[]> { [field.Name] = ["Invalid reference value."] });
                exists = await recordRepo.ExistsAsync(parent, parentId, ct);
            }
            else
            {
                // Custom key: the reference column stores the key field's value — resolve it back to a
                // row Id to confirm a matching, non-deleted parent record exists.
                var typedValue = KeyFieldResolver.ConvertToColumnType(keyField, s);
                if (typedValue is null)
                    throw new ValidationException(new Dictionary<string, string[]> { [field.Name] = ["Invalid reference value."] });
                var col = KeyFieldResolver.ColumnName(keyField);
                var matches = await recordRepo.GetIdsByColumnValuesAsync(parent, col, [typedValue], ct);
                
                exists = matches.Count > 0;
                if (exists)
                {
                    // Swap the custom key string for the physical BIGINT Record ID# so it can be saved.
                    overrides[field.Fid!.Value] = matches.Values.First();
                }
            }

            if (!exists)
                throw new ValidationException(
                    new Dictionary<string, string[]> { [field.Name] = [$"The referenced {parent.Name} record does not exist."] });
        }
        
        return overrides;
    }
}
