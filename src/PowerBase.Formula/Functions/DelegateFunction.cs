using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Functions;

public delegate FormulaType FunctionTypeCheck(IReadOnlyList<FormulaType> argTypes, TextSpan span, List<FormulaDiagnostic> diagnostics);

public delegate FormulaValue FunctionEval(IReadOnlyList<Func<FormulaValue>> args, EvaluationOptions options);

/// <summary>
/// A <see cref="FormulaFunction"/> defined from two delegates. Lets the built-in
/// library be declared compactly without a class per function.
/// </summary>
public sealed class DelegateFunction : FormulaFunction
{
    private readonly FunctionTypeCheck _check;
    private readonly FunctionEval _eval;

    public DelegateFunction(string name, FunctionTypeCheck check, FunctionEval eval)
    {
        Name = name;
        _check = check;
        _eval = eval;
    }

    public override string Name { get; }

    public override FormulaType CheckTypes(IReadOnlyList<FormulaType> argTypes, TextSpan span, List<FormulaDiagnostic> diagnostics)
        => _check(argTypes, span, diagnostics);

    public override FormulaValue Evaluate(IReadOnlyList<Func<FormulaValue>> args, EvaluationOptions options)
        => _eval(args, options);
}
