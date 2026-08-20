namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Generates a guaranteed-unique, immutable AppField.Name from a user-supplied Label.
/// Used by every field-creation path (regular fields, system fields, relationship-created
/// fields) so the C_/S_ naming scheme and collision handling live in exactly one place.
/// </summary>
public interface IFieldNameResolver
{
    /// <summary>
    /// Builds the base name from <paramref name="label"/> (see PowerBase.Domain.Constants.FieldNaming),
    /// then appends a numeric suffix (2, 3, ...) until the result is unique within the table.
    /// </summary>
    Task<string> GenerateUniqueNameAsync(long tableId, string label, bool isSystem, CancellationToken ct = default);
}
