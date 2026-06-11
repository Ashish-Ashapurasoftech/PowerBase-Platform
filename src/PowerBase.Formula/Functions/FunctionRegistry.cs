namespace PowerBase.Formula.Functions;

/// <summary>
/// Case-insensitive name → function map. <see cref="Builtin"/> holds the M1
/// library; the function implementations are registered in
/// <c>Builtins/BuiltinFunctions.cs</c>.
/// </summary>
public sealed class FunctionRegistry : IFunctionRegistry
{
    private readonly Dictionary<string, FormulaFunction> _byName = new(StringComparer.OrdinalIgnoreCase);

    public void Add(FormulaFunction function) => _byName[function.Name] = function;

    public bool TryGet(string name, out FormulaFunction? function) => _byName.TryGetValue(name, out function);

    /// <summary>The shared, immutable-after-construction registry of built-in functions.</summary>
    public static FunctionRegistry Builtin { get; } = BuildBuiltin();

    private static FunctionRegistry BuildBuiltin()
    {
        var registry = new FunctionRegistry();
        Builtins.BuiltinFunctions.RegisterAll(registry);
        return registry;
    }
}
