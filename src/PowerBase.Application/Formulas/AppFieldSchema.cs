using PowerBase.Domain.Entities;
using PowerBase.Formula.Binding;

namespace PowerBase.Application.Formulas;

/// <summary>
/// An <see cref="IFieldSchema"/> built from a table's <see cref="AppField"/> list.
/// Name matching is case-insensitive but space-sensitive (Quickbase semantics).
/// Fields whose type is not referenceable in a scalar formula are simply absent,
/// so the binder reports them as unknown.
/// </summary>
public sealed class AppFieldSchema : IFieldSchema
{
    private readonly Dictionary<string, FieldRef> _byName = new(StringComparer.OrdinalIgnoreCase);

    public AppFieldSchema(IReadOnlyList<AppField> fields)
    {
        foreach (var f in fields)
        {
            if (!f.Fid.HasValue) continue;
            var type = FormulaTypeMap.FieldType(f.TypeCode, f.Settings);
            if (type is null) continue;
            _byName[f.Name] = new FieldRef((long)f.Fid.Value, f.Name, type.Value);
        }
    }

    public bool TryResolve(string fieldName, out FieldRef field) => _byName.TryGetValue(fieldName, out field!);
}
