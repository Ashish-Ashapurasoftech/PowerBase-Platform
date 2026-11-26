using PowerBase.Domain.Entities;
using PowerBase.Formula.Binding;

namespace PowerBase.Application.Formulas;

/// <summary>
/// An <see cref="IFieldSchema"/> built from a table's <see cref="AppField"/> list.
/// Name matching is case-insensitive but space-sensitive (Quickbase semantics).
/// A field can also be referenced by its stable ID — <c>[fid_14]</c> — which keeps
/// resolving after the field is renamed. A real field name always wins over the
/// <c>fid_</c> spelling (labels of that shape are rejected on save, so none should exist).
/// Fields whose type is not referenceable in a scalar formula are simply absent,
/// so the binder reports them as unknown.
/// </summary>
public sealed class AppFieldSchema : IFieldSchema
{
    private readonly Dictionary<string, FieldRef> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, FieldRef> _byFid = new();
    private readonly System.Text.StringBuilder _description = new();
    private string? _signature;

    /// <summary>Hash of everything a formula binds against here (each field's Fid, name, label, type, address flag), for
    /// <see cref="CompiledFormulaCache"/>: two schemas with the same signature bind every expression the same way.</summary>
    public string Signature => _signature ??= CompiledFormulaCache.Hash(_description);

    public AppFieldSchema(IReadOnlyList<AppField> fields)
    {
        foreach (var f in fields)
        {
            if (!f.Fid.HasValue) continue;
            var type = FormulaTypeMap.FieldType(f.TypeCode, f.Settings);
            if (type is null) continue;

            var fid = (long)f.Fid.Value;
            var isAddress = f.TypeCode == "Address";
            _description.Append(fid).Append('|').Append(f.Name.Length).Append(':').Append(f.Name).Append('|').Append(f.Label?.Length ?? -1).Append(':').Append(f.Label).Append('|').Append(type.Value).Append('|').Append(isAddress).Append(';');
            _byFid[fid] = new FieldRef(fid, string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label, type.Value, isAddress);
            // Always register by internal name.
            _byName[f.Name] = new FieldRef(fid, f.Name, type.Value, isAddress);
            // Also register by label when it differs, so [Label] and [Name] both resolve.
            if (!string.IsNullOrWhiteSpace(f.Label) && !string.Equals(f.Label, f.Name, StringComparison.OrdinalIgnoreCase))
                _byName[f.Label] = new FieldRef(fid, f.Label, type.Value, isAddress);
        }
    }

    public bool TryResolve(string fieldName, out FieldRef field)
    {
        if (_byName.TryGetValue(fieldName, out field!)) return true;
        if (FieldIdReference.TryParse(fieldName, out var fid) && _byFid.TryGetValue(fid, out field!)) return true;
        field = null!;
        return false;
    }
}
