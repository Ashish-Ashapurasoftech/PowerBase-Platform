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

            var fid = (long)f.Fid.Value;
            // Always register by internal name.
            _byName[f.Name] = new FieldRef(fid, f.Name, type.Value);
            // Also register by label when it differs, so [Label] and [Name] both resolve.
            if (!string.IsNullOrWhiteSpace(f.Label) && !string.Equals(f.Label, f.Name, StringComparison.OrdinalIgnoreCase))
                _byName[f.Label] = new FieldRef(fid, f.Label, type.Value);
        }
    }

    public bool TryResolve(string fieldName, out FieldRef field) => _byName.TryGetValue(fieldName, out field!);
}
