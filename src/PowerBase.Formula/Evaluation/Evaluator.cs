using PowerBase.Formula.Functions;
using PowerBase.Formula.Syntax;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Evaluation;

/// <summary>
/// Tree-walks a bound/typed AST against one record's values. Operators handle null
/// propagation (arithmetic with a null operand → null; <c>and</c>/<c>or</c>
/// short-circuit); functions receive lazy argument thunks. Assumes the formula
/// compiled without errors.
/// </summary>
internal sealed class Evaluator
{
    private readonly IFunctionRegistry _functions;

    /// <summary>Values of the variables declared by the formula being evaluated. Populated as each
    /// declaration is reached, so an initialiser sees only the ones before it.</summary>
    private readonly Dictionary<string, FormulaValue> _variables = new(StringComparer.OrdinalIgnoreCase);

    public Evaluator(IFunctionRegistry functions) => _functions = functions;

    public FormulaValue Eval(Expr expr, IRecordContext ctx, EvaluationOptions opt) => expr switch
    {
        LiteralExpr l => l.Value,
        FieldRefExpr f => ValueConvert.FromRaw(f.Type, ctx.GetValue(f.Fid)),
        UnaryExpr u => EvalUnary(u, ctx, opt),
        BinaryExpr b => EvalBinary(b, ctx, opt),
        FunctionCallExpr c => EvalCall(c, ctx, opt),
        LetExpr l => EvalLet(l, ctx, opt),
        VariableRefExpr v => EvalVariableRef(v),
        ErrorExpr => throw new FormulaEvaluationException("Cannot evaluate a formula that failed to compile."),
        _ => throw new FormulaEvaluationException($"Unsupported expression node {expr.GetType().Name}."),
    };

    /// <summary>Each declaration is evaluated once, in order, then the body runs with all of them
    /// in scope — so a variable used twice costs one evaluation, which is the point of naming it.</summary>
    private FormulaValue EvalLet(LetExpr l, IRecordContext ctx, EvaluationOptions opt)
    {
        foreach (var decl in l.Declarations)
            _variables[decl.Name] = Eval(decl.Value, ctx, opt);

        return Eval(l.Body, ctx, opt);
    }

    private FormulaValue EvalVariableRef(VariableRefExpr v) =>
        _variables.TryGetValue(v.Name, out var value)
            ? value
            : throw new FormulaEvaluationException($"Variable ${v.Name} was not assigned.");

    private FormulaValue EvalUnary(UnaryExpr u, IRecordContext ctx, EvaluationOptions opt)
    {
        var v = Eval(u.Operand, ctx, opt);
        switch (u.Op)
        {
            case UnaryOp.Not:
                return FormulaValue.Bool(!v.AsBool());
            case UnaryOp.Plus:
                return v;
            case UnaryOp.Negate:
                if (v.IsNull) return FormulaValue.Null(u.Type);
                return v.Type == FormulaType.Duration
                    ? FormulaValue.Duration(v.AsDuration().Negate())
                    : FormulaValue.Number(-v.AsNumber());
            default:
                return FormulaValue.Null(u.Type);
        }
    }

    private FormulaValue EvalBinary(BinaryExpr b, IRecordContext ctx, EvaluationOptions opt)
    {
        // Short-circuit logical operators.
        if (b.Op == BinaryOp.And)
        {
            if (!Eval(b.Left, ctx, opt).AsBool()) return FormulaValue.Bool(false);
            return FormulaValue.Bool(Eval(b.Right, ctx, opt).AsBool());
        }
        if (b.Op == BinaryOp.Or)
        {
            if (Eval(b.Left, ctx, opt).AsBool()) return FormulaValue.Bool(true);
            return FormulaValue.Bool(Eval(b.Right, ctx, opt).AsBool());
        }

        var l = Eval(b.Left, ctx, opt);
        var r = Eval(b.Right, ctx, opt);

        switch (b.Op)
        {
            case BinaryOp.Concat:
                return FormulaValue.Text(ValueConvert.ToTextString(l) + ValueConvert.ToTextString(r));
            case BinaryOp.Add:
            case BinaryOp.Subtract:
            case BinaryOp.Multiply:
            case BinaryOp.Divide:
            case BinaryOp.Power:
                return Arithmetic(b.Op, l, r, b.Type);
            case BinaryOp.Less:
            case BinaryOp.Greater:
            case BinaryOp.LessEqual:
            case BinaryOp.GreaterEqual:
                if (l.IsNull || r.IsNull) return FormulaValue.Bool(false);
                var cmp = ValueOps.Compare(l, r);
                return FormulaValue.Bool(b.Op switch
                {
                    BinaryOp.Less => cmp < 0,
                    BinaryOp.Greater => cmp > 0,
                    BinaryOp.LessEqual => cmp <= 0,
                    _ => cmp >= 0,
                });
            case BinaryOp.Equal:
                return FormulaValue.Bool(ValueOps.AreEqual(l, r));
            case BinaryOp.NotEqual:
                return FormulaValue.Bool(!ValueOps.AreEqual(l, r));
            default:
                return FormulaValue.Null(b.Type);
        }
    }

    private static FormulaValue Arithmetic(BinaryOp op, FormulaValue l, FormulaValue r, FormulaType resultType)
    {
        if (l.IsNull || r.IsNull) return FormulaValue.Null(resultType);

        var lt = l.Type;
        var rt = r.Type;

        if (lt == FormulaType.Number && rt == FormulaType.Number)
        {
            decimal a = l.AsNumber(), b = r.AsNumber();
            return op switch
            {
                BinaryOp.Add => FormulaValue.Number(a + b),
                BinaryOp.Subtract => FormulaValue.Number(a - b),
                BinaryOp.Multiply => FormulaValue.Number(a * b),
                BinaryOp.Divide => b == 0 ? FormulaValue.Null(FormulaType.Number) : FormulaValue.Number(a / b),
                BinaryOp.Power => Pow(a, b),
                _ => FormulaValue.Null(FormulaType.Number),
            };
        }

        switch (op)
        {
            case BinaryOp.Add:
                if (lt == FormulaType.Duration && rt == FormulaType.Duration) return FormulaValue.Duration(l.AsDuration() + r.AsDuration());
                if (lt == FormulaType.Date && rt == FormulaType.Duration) return FormulaValue.Date(AddToDate(l.AsDate(), r.AsDuration()));
                if (lt == FormulaType.Duration && rt == FormulaType.Date) return FormulaValue.Date(AddToDate(r.AsDate(), l.AsDuration()));
                if (lt == FormulaType.DateTime && rt == FormulaType.Duration) return FormulaValue.DateTime(l.AsDateTime() + r.AsDuration());
                if (lt == FormulaType.Duration && rt == FormulaType.DateTime) return FormulaValue.DateTime(r.AsDateTime() + l.AsDuration());
                break;
            case BinaryOp.Subtract:
                if (lt == FormulaType.Duration && rt == FormulaType.Duration) return FormulaValue.Duration(l.AsDuration() - r.AsDuration());
                if (lt == FormulaType.Date && rt == FormulaType.Duration) return FormulaValue.Date(AddToDate(l.AsDate(), -r.AsDuration()));
                if (lt == FormulaType.DateTime && rt == FormulaType.Duration) return FormulaValue.DateTime(l.AsDateTime() - r.AsDuration());
                if (lt == FormulaType.Date && rt == FormulaType.Date) return FormulaValue.Duration(l.AsDate().ToDateTime(TimeOnly.MinValue) - r.AsDate().ToDateTime(TimeOnly.MinValue));
                if (lt == FormulaType.DateTime && rt == FormulaType.DateTime) return FormulaValue.Duration(l.AsDateTime() - r.AsDateTime());
                break;
            case BinaryOp.Multiply:
                if (lt == FormulaType.Duration && rt == FormulaType.Number) return FormulaValue.Duration(l.AsDuration() * (double)r.AsNumber());
                if (lt == FormulaType.Number && rt == FormulaType.Duration) return FormulaValue.Duration(r.AsDuration() * (double)l.AsNumber());
                break;
            case BinaryOp.Divide:
                if (lt == FormulaType.Duration && rt == FormulaType.Number)
                    return r.AsNumber() == 0 ? FormulaValue.Null(FormulaType.Duration) : FormulaValue.Duration(l.AsDuration() / (double)r.AsNumber());
                break;
        }

        return FormulaValue.Null(resultType);
    }

    private static FormulaValue Pow(decimal a, decimal b)
    {
        try
        {
            return FormulaValue.Number((decimal)Math.Pow((double)a, (double)b));
        }
        catch (OverflowException)
        {
            return FormulaValue.Null(FormulaType.Number);
        }
    }

    private static DateOnly AddToDate(DateOnly date, TimeSpan span)
        => DateOnly.FromDateTime(date.ToDateTime(TimeOnly.MinValue) + span);

    private FormulaValue EvalCall(FunctionCallExpr c, IRecordContext ctx, EvaluationOptions opt)
    {
        if (!_functions.TryGet(c.Name, out var fn) || fn is null)
            throw new FormulaEvaluationException($"Unknown function '{c.Name}'.");

        var thunks = new Func<FormulaValue>[c.Args.Count];
        for (int i = 0; i < c.Args.Count; i++)
        {
            var arg = c.Args[i];
            thunks[i] = () => Eval(arg, ctx, opt);
        }

        var result = fn.Evaluate(thunks, opt, ctx);

        // A typeless null (e.g. Case with no match/default) adopts the call's static type.
        if (result.IsNull && result.Type == FormulaType.Null && c.Type != FormulaType.Null)
            return FormulaValue.Null(c.Type);

        return result;
    }
}
