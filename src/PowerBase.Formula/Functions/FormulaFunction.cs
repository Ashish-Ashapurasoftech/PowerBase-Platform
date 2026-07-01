using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Functions;

/// <summary>
/// A formula function: its name, a static type-check rule, and a runtime
/// implementation. The type checker calls <see cref="CheckTypes"/> to resolve a
/// call's result type (and report bad arity/argument types); the evaluator calls
/// <see cref="Evaluate"/>.
/// </summary>
/// <remarks>
/// Arguments are passed to <see cref="Evaluate"/> as thunks, not values, so that
/// control-flow functions (<c>If</c>, <c>Case</c>, <c>Nz</c>) can evaluate only
/// the branches they need. Most functions are eager and simply invoke every thunk.
/// </remarks>
public abstract class FormulaFunction
{
    public abstract string Name { get; }

    /// <summary>
    /// Validate the call given its argument types and return the call's result
    /// type. Append diagnostics for arity/type problems; return a best-effort type
    /// so downstream checking can continue.
    /// </summary>
    public abstract FormulaType CheckTypes(IReadOnlyList<FormulaType> argTypes, TextSpan span, List<FormulaDiagnostic> diagnostics);

    /// <summary>Evaluate the call. Each argument is a thunk; invoke only those you need.</summary>
    public abstract FormulaValue Evaluate(IReadOnlyList<Func<FormulaValue>> args, EvaluationOptions options);

    /// <summary>
    /// Context-aware evaluation, used by cross-table functions that need to read other
    /// records via <paramref name="context"/>. Defaults to the context-free overload, so
    /// ordinary scalar functions need not implement it.
    /// </summary>
    public virtual FormulaValue Evaluate(IReadOnlyList<Func<FormulaValue>> args, EvaluationOptions options, IRecordContext context)
        => Evaluate(args, options);
}
