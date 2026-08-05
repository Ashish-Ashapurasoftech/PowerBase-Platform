using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Syntax;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Checking;

/// <summary>
/// Assigns and verifies a <see cref="FormulaType"/> for every AST node. Mirrors
/// Quickbase's strict typing: there are no implicit conversions, so e.g. text and
/// number cannot be added or concatenated without an explicit <c>ToText</c>/
/// <c>ToNumber</c>. Type errors are reported but the checker still returns a
/// best-effort type so it can keep going and surface multiple problems.
/// Run after the <see cref="Binding.Binder"/> (field nodes must already be typed).
/// </summary>
public sealed class TypeChecker
{
    private readonly IFunctionRegistry _functions;
    private readonly List<FormulaDiagnostic> _diags;

    /// <summary>Types of the variables declared so far, by name. Scoping is flat and strictly
    /// in order: a declaration can use the ones above it and nothing else.</summary>
    private readonly Dictionary<string, FormulaType> _variables = new(StringComparer.OrdinalIgnoreCase);

    public TypeChecker(IFunctionRegistry functions, List<FormulaDiagnostic> diagnostics)
    {
        _functions = functions;
        _diags = diagnostics;
    }

    public FormulaType Check(Expr expr)
    {
        var type = CheckCore(expr);
        expr.Type = type;
        return type;
    }

    private FormulaType CheckCore(Expr expr) => expr switch
    {
        LiteralExpr l => l.Type,
        FieldRefExpr f => f.Type,        // set by the binder; Null when unresolved
        ErrorExpr => FormulaType.Null,
        UnaryExpr u => CheckUnary(u),
        BinaryExpr b => CheckBinary(b),
        FunctionCallExpr c => CheckCall(c),
        LetExpr l => CheckLet(l),
        VariableRefExpr v => CheckVariableRef(v),
        _ => FormulaType.Null,
    };

    private FormulaType CheckLet(LetExpr l)
    {
        foreach (var decl in l.Declarations)
        {
            // Checked before it is in scope, so a declaration can't refer to itself.
            var valueType = Check(decl.Value);

            if (string.IsNullOrEmpty(decl.Name))
                continue; // the parser already reported the missing name

            if (!_variables.TryAdd(decl.Name, valueType))
                _diags.Add(new FormulaDiagnostic(
                    FormulaErrorCode.TypeMismatch,
                    $"Variable '{decl.Name}' is declared more than once.",
                    decl.Span));
        }

        return Check(l.Body);
    }

    private FormulaType CheckVariableRef(VariableRefExpr v)
    {
        if (_variables.TryGetValue(v.Name, out var type))
            return type;

        _diags.Add(new FormulaDiagnostic(
            FormulaErrorCode.UnknownField,
            $"Unknown variable ${v.Name}.",
            v.Span));
        return FormulaType.Null;
    }

    // An operand that already failed (Null) shouldn't trigger a second, cascading error.
    private static bool IsError(FormulaType t) => t == FormulaType.Null;

    private FormulaType CheckUnary(UnaryExpr u)
    {
        var t = Check(u.Operand);
        if (IsError(t)) return u.Op == UnaryOp.Not ? FormulaType.Bool : FormulaType.Number;

        switch (u.Op)
        {
            case UnaryOp.Negate:
            case UnaryOp.Plus:
                if (t is FormulaType.Number) return FormulaType.Number;
                if (t is FormulaType.Duration) return FormulaType.Duration;
                break;
            case UnaryOp.Not:
                if (t is FormulaType.Bool) return FormulaType.Bool;
                break;
        }

        Mismatch(u.Span, $"Operator '{Sym(u.Op)}' cannot be applied to {t}.");
        return u.Op == UnaryOp.Not ? FormulaType.Bool : FormulaType.Number;
    }

    private FormulaType CheckBinary(BinaryExpr b)
    {
        var lt = Check(b.Left);
        var rt = Check(b.Right);

        return b.Op switch
        {
            BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide or BinaryOp.Power
                => CheckArithmetic(b, lt, rt),
            BinaryOp.Concat => CheckConcat(b, lt, rt),
            BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or BinaryOp.GreaterEqual
                => CheckComparison(b, lt, rt),
            BinaryOp.Equal or BinaryOp.NotEqual => CheckEquality(b, lt, rt),
            BinaryOp.And or BinaryOp.Or => CheckLogical(b, lt, rt),
            _ => FormulaType.Null,
        };
    }

    private FormulaType CheckArithmetic(BinaryExpr b, FormulaType lt, FormulaType rt)
    {
        if (IsError(lt) || IsError(rt)) return FormulaType.Number;
        var result = ResolveArithmetic(b.Op, lt, rt);
        if (result is null)
        {
            Mismatch(b.Span, $"Operator '{Sym(b.Op)}' cannot be applied to {lt} and {rt}.");
            return FormulaType.Number;
        }
        return result.Value;
    }

    private static FormulaType? ResolveArithmetic(BinaryOp op, FormulaType l, FormulaType r)
    {
        static bool N(FormulaType t) => t == FormulaType.Number;
        static bool D(FormulaType t) => t == FormulaType.Duration;
        static bool Dt(FormulaType t) => t is FormulaType.Date or FormulaType.DateTime;

        switch (op)
        {
            case BinaryOp.Add:
                if (N(l) && N(r)) return FormulaType.Number;
                if (D(l) && D(r)) return FormulaType.Duration;
                if (Dt(l) && D(r)) return l;   // Date/DateTime + Duration → same date type
                if (D(l) && Dt(r)) return r;
                return null;
            case BinaryOp.Subtract:
                if (N(l) && N(r)) return FormulaType.Number;
                if (D(l) && D(r)) return FormulaType.Duration;
                if (Dt(l) && D(r)) return l;   // Date - Duration → Date
                if (l == FormulaType.Date && r == FormulaType.Date) return FormulaType.Duration;
                if (l == FormulaType.DateTime && r == FormulaType.DateTime) return FormulaType.Duration;
                return null;
            case BinaryOp.Multiply:
                if (N(l) && N(r)) return FormulaType.Number;
                if (D(l) && N(r)) return FormulaType.Duration;
                if (N(l) && D(r)) return FormulaType.Duration;
                return null;
            case BinaryOp.Divide:
                if (N(l) && N(r)) return FormulaType.Number;
                if (D(l) && N(r)) return FormulaType.Duration;
                return null;
            case BinaryOp.Power:
                if (N(l) && N(r)) return FormulaType.Number;
                return null;
            default:
                return null;
        }
    }

    private FormulaType CheckConcat(BinaryExpr b, FormulaType lt, FormulaType rt)
    {
        if (IsError(lt) || IsError(rt)) return FormulaType.Text;
        if (lt == FormulaType.Text && rt == FormulaType.Text) return FormulaType.Text;
        Mismatch(b.Span, $"'&' requires text on both sides (got {lt} and {rt}); wrap non-text values in ToText().");
        return FormulaType.Text;
    }

    private FormulaType CheckComparison(BinaryExpr b, FormulaType lt, FormulaType rt)
    {
        if (IsError(lt) || IsError(rt)) return FormulaType.Bool;
        if (lt == rt && IsOrderable(lt)) return FormulaType.Bool;
        if ((lt == FormulaType.Number && rt == FormulaType.Duration) || 
            (lt == FormulaType.Duration && rt == FormulaType.Number))
            return FormulaType.Bool;
        Mismatch(b.Span, $"Operator '{Sym(b.Op)}' cannot compare {lt} and {rt}.");
        return FormulaType.Bool;
    }

    private FormulaType CheckEquality(BinaryExpr b, FormulaType lt, FormulaType rt)
    {
        if (IsError(lt) || IsError(rt)) return FormulaType.Bool;
        if (lt == rt && IsEquatable(lt)) return FormulaType.Bool;
        if ((lt == FormulaType.Number && rt == FormulaType.Duration) || 
            (lt == FormulaType.Duration && rt == FormulaType.Number))
            return FormulaType.Bool;
        Mismatch(b.Span, $"Operator '{Sym(b.Op)}' cannot compare {lt} and {rt}.");
        return FormulaType.Bool;
    }

    private FormulaType CheckLogical(BinaryExpr b, FormulaType lt, FormulaType rt)
    {
        if (IsError(lt) || IsError(rt)) return FormulaType.Bool;
        if (lt == FormulaType.Bool && rt == FormulaType.Bool) return FormulaType.Bool;
        Mismatch(b.Span, $"Operator '{Sym(b.Op)}' requires boolean operands (got {lt} and {rt}).");
        return FormulaType.Bool;
    }

    private FormulaType CheckCall(FunctionCallExpr c)
    {
        var argTypes = c.Args.Select(Check).ToList();
        if (_functions.TryGet(c.Name, out var fn) && fn is not null)
            return fn.CheckTypes(argTypes, c.Span, _diags);

        _diags.Add(new FormulaDiagnostic(FormulaErrorCode.UnknownFunction, $"Unknown function '{c.Name}'.", c.Span));
        return FormulaType.Null;
    }

    private static bool IsOrderable(FormulaType t) =>
        t is FormulaType.Number or FormulaType.Date or FormulaType.DateTime or FormulaType.Duration or FormulaType.Text;

    private static bool IsEquatable(FormulaType t) =>
        t is FormulaType.Number or FormulaType.Text or FormulaType.Bool
          or FormulaType.Date or FormulaType.DateTime or FormulaType.Duration or FormulaType.User;

    private void Mismatch(TextSpan span, string message) =>
        _diags.Add(new FormulaDiagnostic(FormulaErrorCode.TypeMismatch, message, span));

    private static string Sym(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+", BinaryOp.Subtract => "-", BinaryOp.Multiply => "*", BinaryOp.Divide => "/",
        BinaryOp.Power => "^", BinaryOp.Concat => "&",
        BinaryOp.Equal => "=", BinaryOp.NotEqual => "<>",
        BinaryOp.Less => "<", BinaryOp.Greater => ">", BinaryOp.LessEqual => "<=", BinaryOp.GreaterEqual => ">=",
        BinaryOp.And => "and", BinaryOp.Or => "or",
        _ => "?",
    };

    private static string Sym(UnaryOp op) => op switch
    {
        UnaryOp.Negate => "-",
        UnaryOp.Plus => "+",
        UnaryOp.Not => "not",
        _ => "?",
    };
}
