namespace PowerBase.Formula.Functions;

/// <summary>
/// Resolves function names (case-insensitive) to their <see cref="FormulaFunction"/>.
/// Open by design: Tier-2 aggregation/query functions register here later without
/// touching the type checker or evaluator.
/// </summary>
public interface IFunctionRegistry
{
    bool TryGet(string name, out FormulaFunction? function);
}
