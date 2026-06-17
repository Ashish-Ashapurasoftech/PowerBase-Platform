using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>Validates that submitted Reference field values point at existing parent records.</summary>
public static class ReferenceWriteValidator
{
    public static async Task ValidateAsync(
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> values,
        IAppTableRepository tableRepo,
        IRecordRepository recordRepo,
        CancellationToken ct)
    {
        foreach (var field in fields.Where(f => f.TypeCode == "Reference" && f.Fid.HasValue))
        {
            if (!values.TryGetValue(field.Fid!.Value, out var raw) || raw is null) continue;
            var s = raw.ToString();
            if (string.IsNullOrWhiteSpace(s)) continue;

            if (!long.TryParse(s, out var parentId))
                throw new ValidationException(new Dictionary<string, string[]> { [field.Name] = ["Invalid reference value."] });

            var settings = FormulaTypeMap.ParseReferenceSettings(field.Settings);
            if (settings?.ParentTableId is not long parentTableId) continue;

            var parent = await tableRepo.GetByIdAsync(parentTableId, ct);
            if (!await recordRepo.ExistsAsync(parent, parentId, ct))
                throw new ValidationException(
                    new Dictionary<string, string[]> { [field.Name] = [$"The referenced {parent.Name} record does not exist."] });
        }
    }
}
