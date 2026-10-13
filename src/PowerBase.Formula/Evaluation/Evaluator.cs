using PowerBase.Formula.Binding;
using PowerBase.Formula.Checking;
using PowerBase.Formula.Diagnostics;
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

    /// <summary>When true, operand and argument types are re-checked from the values actually produced.
    /// Only formulas that read a field chosen per record (<c>GetFieldValue</c> with a per-record ID) run
    /// this way — everything else was fully type-checked at compile time and takes the plain path.</summary>
    private readonly bool _verifyTypes;

    public Evaluator(IFunctionRegistry functions, bool verifyTypes = false)
    {
        _functions = functions;
        _verifyTypes = verifyTypes;
    }

    public FormulaValue Eval(Expr expr, IRecordContext ctx, EvaluationOptions opt) => expr switch
    {
        LiteralExpr l => l.Value,
        FieldRefExpr { TableAliasId: { } tableId } => FormulaValue.Text(tableId),
        FieldRefExpr { VariableValue: { } variableValue } => FormulaValue.Text(variableValue),
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
        {
            var value = Eval(decl.Value, ctx, opt);
            if (_verifyTypes) VerifyDeclaredType(decl, value);
            _variables[decl.Name] = value;
        }

        return Eval(l.Body, ctx, opt);
    }

    private FormulaValue EvalVariableRef(VariableRefExpr v) =>
        _variables.TryGetValue(v.Name, out var value)
            ? value
            : throw new FormulaEvaluationException($"Variable ${v.Name} was not assigned.");

    private FormulaValue EvalUnary(UnaryExpr u, IRecordContext ctx, EvaluationOptions opt)
    {
        var v = Eval(u.Operand, ctx, opt);
        if (_verifyTypes && v.Type != FormulaType.Null && TypeChecker.UnaryTypeError(u.Op, v.Type) is { } unaryError)
            throw new FormulaEvaluationException(unaryError);

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
        if (_verifyTypes && b.Op is BinaryOp.And or BinaryOp.Or)
            return EvalLogicalVerified(b, ctx, opt);

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
        if (_verifyTypes) VerifyBinaryTypes(b.Op, l.Type, r.Type);

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
                if (lt == FormulaType.Time && rt == FormulaType.Duration) return FormulaValue.Time(l.AsTime().Add(r.AsDuration()));
                if (lt == FormulaType.Duration && rt == FormulaType.Time) return FormulaValue.Time(r.AsTime().Add(l.AsDuration()));
                break;
            case BinaryOp.Subtract:
                if (lt == FormulaType.Duration && rt == FormulaType.Duration) return FormulaValue.Duration(l.AsDuration() - r.AsDuration());
                if (lt == FormulaType.Date && rt == FormulaType.Duration) return FormulaValue.Date(AddToDate(l.AsDate(), -r.AsDuration()));
                if (lt == FormulaType.DateTime && rt == FormulaType.Duration) return FormulaValue.DateTime(l.AsDateTime() - r.AsDuration());
                if (lt == FormulaType.Date && rt == FormulaType.Date) return FormulaValue.Duration(l.AsDate().ToDateTime(TimeOnly.MinValue) - r.AsDate().ToDateTime(TimeOnly.MinValue));
                if (lt == FormulaType.DateTime && rt == FormulaType.DateTime) return FormulaValue.Duration(l.AsDateTime() - r.AsDateTime());
                if (lt == FormulaType.Time && rt == FormulaType.Duration) return FormulaValue.Time(l.AsTime().Add(-r.AsDuration()));
                if (lt == FormulaType.Time && rt == FormulaType.Time) return FormulaValue.Duration(l.AsTime().ToTimeSpan() - r.AsTime().ToTimeSpan());
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

    // ── Type verification (only for formulas that read a per-record field ID) ──────────────

    private static void VerifyBinaryTypes(BinaryOp op, FormulaType lt, FormulaType rt)
    {
        if (lt == FormulaType.Null || rt == FormulaType.Null) return; // typeless blank — nothing to verify
        if (TypeChecker.BinaryTypeError(op, lt, rt) is { } error) throw new FormulaEvaluationException(error);
    }

    /// <summary>and/or with the same short-circuiting as the plain path, but rejecting a non-boolean operand
    /// instead of failing on it.</summary>
    private FormulaValue EvalLogicalVerified(BinaryExpr b, IRecordContext ctx, EvaluationOptions opt)
    {
        var isAnd = b.Op == BinaryOp.And;
        var left = Eval(b.Left, ctx, opt);
        VerifyBinaryTypes(b.Op, left.Type, b.Right.Type == FormulaType.Null ? FormulaType.Bool : b.Right.Type);

        if (isAnd ? !left.AsBool() : left.AsBool()) return FormulaValue.Bool(!isAnd);

        var right = Eval(b.Right, ctx, opt);
        VerifyBinaryTypes(b.Op, left.Type, right.Type);
        return FormulaValue.Bool(right.AsBool());
    }

    private static void VerifyDeclaredType(VariableDeclaration decl, FormulaValue value)
    {
        if (string.IsNullOrEmpty(decl.DeclaredType) || value.Type == FormulaType.Null) return;
        if (VariableTypes.TryParse(decl.DeclaredType, out var declared) && value.Type != declared)
            throw new FormulaEvaluationException($"Expecting {VariableTypes.Keyword(declared)} but found {VariableTypes.Keyword(value.Type)}.");
    }

    /// <summary>The first type problem the function's own checker finds for the argument types seen at run time.</summary>
    private static string? CallTypeError(FormulaFunction fn, FunctionCallExpr c, FormulaType[] argTypes)
    {
        var diagnostics = new List<FormulaDiagnostic>();
        fn.CheckTypes(argTypes, c.Span, diagnostics);
        return diagnostics.FirstOrDefault(d => d.Severity == FormulaSeverity.Error)?.Message;
    }

    /// <summary><c>GetFieldValue(&lt;id&gt;)</c> whose ID is only known now: look the field up and read it as that field's type.
    /// A blank ID gives a blank result; an ID that names no readable field is an error.</summary>
    private FormulaValue EvalDynamicFieldValue(FunctionCallExpr c, IRecordContext ctx, EvaluationOptions opt)
    {
        var idValue = Eval(c.Args[0], ctx, opt);
        if (idValue.IsNull) return FormulaValue.Null(FormulaType.Null);

        if (!FieldValueCall.TryGetFieldNumber(idValue, out var fid))
            throw new FormulaEvaluationException($"{FieldValueCall.Name}: {ValueConvert.ToTextString(idValue)} isn't a field ID (use a number like 14 or text like \"fid_14\").");

        if (opt.FieldTypes is null) return FormulaValue.Null(FormulaType.Null); // this host can't resolve a field named at run time

        if (!opt.FieldTypes.TryGetValue(fid, out var type))
            throw new FormulaEvaluationException($"{FieldValueCall.Name}: field fid_{fid} doesn't exist on this table or can't be read by a formula.");

        return ValueConvert.FromRaw(type, ctx.GetValue(fid));
    }

    private FormulaValue EvalCall(FunctionCallExpr c, IRecordContext ctx, EvaluationOptions opt)
    {
        // GetFieldValue(<id>): read the field the binder resolved, exactly as [Field] would.
        if (c.ResolvedField is { } field)
            return ValueConvert.FromRaw(field.Type, ctx.GetValue(field.Fid));

        if (c.IsDynamicFieldValue)
            return EvalDynamicFieldValue(c, ctx, opt);

        if (!_functions.TryGet(c.Name, out var fn) || fn is null)
            throw new FormulaEvaluationException($"Unknown function '{c.Name}'.");

        // When verifying, remember the type each argument actually produced (the static type stands in for
        // any argument the function never evaluated).
        var argTypes = _verifyTypes ? c.Args.Select(a => a.Type).ToArray() : null;

        var thunks = new Func<FormulaValue>[c.Args.Count];
        for (int i = 0; i < c.Args.Count; i++)
        {
            var arg = c.Args[i];
            var index = i;
            thunks[i] = argTypes is null
                ? () => Eval(arg, ctx, opt)
                : () =>
                {
                    var value = Eval(arg, ctx, opt);
                    if (value.Type != FormulaType.Null) argTypes[index] = value.Type;
                    return value;
                };
        }

        FormulaValue result;
        try
        {
            result = fn.Evaluate(thunks, opt, ctx);
        }
        catch (InvalidCastException) when (argTypes is not null)
        {
            // An argument of the wrong type tripped the function — say which, using the function's own rules.
            throw new FormulaEvaluationException(CallTypeError(fn, c, argTypes) ?? $"{c.Name} was given a value of an unexpected type.");
        }

        if (argTypes is not null && CallTypeError(fn, c, argTypes) is { } callError)
            throw new FormulaEvaluationException(callError);

        // A typeless null (e.g. Case with no match/default) adopts the call's static type.
        if (result.IsNull && result.Type == FormulaType.Null && c.Type != FormulaType.Null)
            return FormulaValue.Null(c.Type);

        return result;
    }
}
