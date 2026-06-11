using PowerBase.Formula.Binding;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

/// <summary>In-memory field schema for tests. Case-insensitive, space-sensitive names.</summary>
public sealed class TestSchema : IFieldSchema
{
    private readonly Dictionary<string, FieldRef> _fields = new(StringComparer.OrdinalIgnoreCase);
    private long _nextFid = 1;

    public TestSchema Add(string name, FormulaType type)
    {
        _fields[name] = new FieldRef(_nextFid++, name, type);
        return this;
    }

    public bool TryResolve(string fieldName, out FieldRef field) => _fields.TryGetValue(fieldName, out field!);
}
