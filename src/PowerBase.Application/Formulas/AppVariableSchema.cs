using PowerBase.Domain.Entities;
using PowerBase.Formula.Binding;

namespace PowerBase.Application.Formulas;

/// <summary>
/// An <see cref="IVariableSchema"/> built from an app's variables. A name matches case-insensitively, like a
/// field name; a variable can also be named by its id (<c>[var_5]</c>), which keeps resolving after the
/// variable is renamed. A real variable name always wins over the <c>var_</c> spelling (names of that shape
/// are rejected when a variable is saved, so none should exist).
/// </summary>
public sealed class AppVariableSchema : IVariableSchema
{
    private readonly Dictionary<string, VariableRef> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, VariableRef> _byId = new();

    public AppVariableSchema(IEnumerable<AppVariable> variables)
    {
        foreach (var v in variables)
        {
            var variable = new VariableRef(v.Id, v.Name, v.Value);
            _byId[v.Id] = variable;
            _byName[v.Name.Trim()] = variable;
        }
    }

    public bool TryResolve(string name, out VariableRef variable)
    {
        if (_byName.TryGetValue(name.Trim(), out variable!)) return true;
        if (VariableIdReference.TryParse(name, out var id) && _byId.TryGetValue(id, out variable!)) return true;
        variable = null!;
        return false;
    }
}
