using PowerBase.Formula.Types;

namespace PowerBase.Formula.Binding;

/// <summary>
/// A resolved field reference: the field's stable id (Fid), its display name, and
/// the <see cref="FormulaType"/> its values present to a formula.
/// </summary>
public sealed record FieldRef(long Fid, string Name, FormulaType Type);

/// <summary>
/// Resolves <c>[Field Name]</c> references during binding. Name matching is
/// case-insensitive but space-sensitive (matching Quickbase). Implementations are
/// provided by the host (e.g. built from a table's <c>AppField</c> list); the
/// engine itself stays free of PowerBase domain types.
/// </summary>
public interface IFieldSchema
{
    /// <summary>Resolve a field by display name. Returns false for unknown fields.</summary>
    bool TryResolve(string fieldName, out FieldRef field);
}
