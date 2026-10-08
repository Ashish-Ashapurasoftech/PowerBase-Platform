using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Checks, before a pipeline writes a value into a Reference field, that the parent table the field points
/// at exists in the current database. The field stores that table's numeric id in its settings; after an
/// app is copied to another account or tenant the id can name a table this database doesn't have, and the
/// write would otherwise die deep inside the reference validator with a bare "Table 'N' was not found" —
/// which the engine then retried with backoff. Fails the step once, naming the field.
/// </summary>
public static class PipelineReferenceTargets
{
    public static async Task EnsureAvailableAsync(
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> values,
        IAppTableRepository tableRepo,
        CancellationToken ct)
    {
        foreach (var field in fields.Where(f => f.Fid.HasValue && f.TypeCode == "Reference"))
        {
            if (!values.TryGetValue(field.Fid!.Value, out var value) || value is null || string.IsNullOrWhiteSpace(value.ToString())) continue;
            if (FormulaTypeMap.ParseReferenceSettings(field.Settings)?.ParentTableId is not long parentTableId) continue;
            try
            {
                await tableRepo.GetByIdAsync(parentTableId, ct);
            }
            catch (NotFoundException)
            {
                throw new PipelineNonRetryableException(
                    $"Cannot set '{(!string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name)}' (Reference): the field points to a table " +
                    "that is not available in this account (its relationship settings refer to a table that does not exist here — " +
                    "for example after the app was copied from another account). Recreate the field's relationship, or remove the field from this step.");
            }
        }
    }
}
