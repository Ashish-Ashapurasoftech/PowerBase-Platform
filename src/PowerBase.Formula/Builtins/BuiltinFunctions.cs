using PowerBase.Formula.Functions;

namespace PowerBase.Formula.Builtins;

/// <summary>Registers the full M1 function library into a <see cref="FunctionRegistry"/>.</summary>
internal static class BuiltinFunctions
{
    public static void RegisterAll(FunctionRegistry registry)
    {
        LogicalFunctions.Register(registry);
        TextFunctions.Register(registry);
        NumberFunctions.Register(registry);
        DateFunctions.Register(registry);
        UserFunctions.Register(registry);
        ListFunctions.Register(registry);
        SystemFunctions.Register(registry);
        RecordFunctions.Register(registry);
    }
}
