using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Functions;

public delegate FormulaType FunctionTypeCheck(IReadOnlyList<FormulaType> argTypes, TextSpan span, List<FormulaDiagnostic> diagnostics);

public delegate FormulaValue FunctionEval(IReadOnlyList<Func<FormulaValue>> args, EvaluationOptions options);

public delegate FormulaValue FunctionEvalWithContext(IReadOnlyList<Func<FormulaValue>> args, EvaluationOptions options, IRecordContext context);

/// <summary>
/// A <see cref="FormulaFunction"/> defined from two delegates. Lets the built-in
/// library be declared compactly without a class per function.
/// </summary>
public sealed class DelegateFunction : FormulaFunction
{
    private readonly FunctionTypeCheck _check;
    private readonly FunctionEval? _eval;
    private readonly FunctionEvalWithContext? _evalCtx;

    public DelegateFunction(string name, FunctionTypeCheck check, FunctionEval eval)
    {
        Name = name;
        _check = check;
        _eval = eval;
    }

    public DelegateFunction(string name, FunctionTypeCheck check, FunctionEvalWithContext eval)
    {
        Name = name;
        _check = check;
        _evalCtx = eval;
    }

    public override string Name { get; }

    public override FormulaType CheckTypes(IReadOnlyList<FormulaType> argTypes, TextSpan span, List<FormulaDiagnostic> diagnostics)
        => _check(argTypes, span, diagnostics);

    public override FormulaValue Evaluate(IReadOnlyList<Func<FormulaValue>> args, EvaluationOptions options)
        => _eval is not null ? _eval(args, options) : Evaluate(args, options, NullRecordContext.Instance);

    public override FormulaValue Evaluate(IReadOnlyList<Func<FormulaValue>> args, EvaluationOptions options, IRecordContext context)
        => _evalCtx is not null ? _evalCtx(args, options, context) : _eval!(args, options);

    private sealed class NullRecordContext : IRecordContext
    {
        public static readonly NullRecordContext Instance = new();
        public object? GetValue(long fid) => null;
    }
}
