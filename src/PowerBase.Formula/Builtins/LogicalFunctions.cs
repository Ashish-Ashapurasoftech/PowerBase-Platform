using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

internal static class LogicalFunctions
{
    public static void Register(FunctionRegistry r)
    {
        // The else branch is optional: If(cond, value) yields blank when the condition is false.
        // Quickbase's If behaves this way and CheckIf already types a missing third argument as
        // Null, so only the arity check stood in the way.
        r.Add(Fn.Lazy("If", CheckIf, (t, _) =>
            t[0]().AsBool() ? t[1]() : (t.Count > 2 ? t[2]() : FormulaValue.Null(FormulaType.Null))));

        r.Add(Fn.Lazy("Case", CheckCase, EvalCase));

        r.Add(Fn.Exact("Not", FormulaType.Bool, new[] { P.Bool }, (a, _) => FormulaValue.Bool(!a[0].AsBool())));

        r.Add(Fn.Lazy("And", CheckBoolVariadic("And"),
            (t, _) => { foreach (var th in t) if (!th().AsBool()) return FormulaValue.Bool(false); return FormulaValue.Bool(true); }));

        r.Add(Fn.Lazy("Or", CheckBoolVariadic("Or"),
            (t, _) => { foreach (var th in t) if (th().AsBool()) return FormulaValue.Bool(true); return FormulaValue.Bool(false); }));

        r.Add(Fn.Exact("IsNull", FormulaType.Bool, new[] { P.Any }, (a, _) => FormulaValue.Bool(a[0].IsNull)));
        r.Add(Fn.Exact("IsNotNull", FormulaType.Bool, new[] { P.Any }, (a, _) => FormulaValue.Bool(!a[0].IsNull)));

        r.Add(Fn.Lazy("Nz", CheckNz,
            (t, _) => { var v = t[0](); return !v.IsNull ? v : (t.Count == 2 ? t[1]() : FormulaValue.Number(0)); }));

        r.Add(Fn.Exact("ToBool", FormulaType.Bool, new[] { P.Any }, (a, _) => ValueConvert.ToBoolValue(a[0])));

        r.Add(Fn.Nullary("True", FormulaType.Bool, _ => FormulaValue.Bool(true)));
        r.Add(Fn.Nullary("False", FormulaType.Bool, _ => FormulaValue.Bool(false)));
    }

    private static FormulaType CheckIf(IReadOnlyList<FormulaType> a, TextSpan span, List<FormulaDiagnostic> d)
    {
        Fn.RequireArity("If", a.Count, 2, 3, span, d);
        if (a.Count >= 1) Fn.RequireParam("If", 0, P.Bool, a[0], span, d);
        var t1 = a.Count > 1 ? a[1] : FormulaType.Null;
        var t2 = a.Count > 2 ? a[2] : FormulaType.Null;
        var common = Fn.CommonType(t1, t2);
        if (common == FormulaType.Null && t1 != FormulaType.Null && t2 != FormulaType.Null)
        {
            d.Add(new FormulaDiagnostic(FormulaErrorCode.TypeMismatch, $"If branches must have the same type (got {t1} and {t2}).", span));
            return t1;
        }
        return common;
    }

    private static FunctionTypeCheck CheckBoolVariadic(string name) => (a, span, d) =>
    {
        Fn.RequireArity(name, a.Count, 2, int.MaxValue, span, d);
        for (int i = 0; i < a.Count; i++) Fn.RequireParam(name, i, P.Bool, a[i], span, d);
        return FormulaType.Bool;
    };

    private static FormulaType CheckNz(IReadOnlyList<FormulaType> a, TextSpan span, List<FormulaDiagnostic> d)
    {
        Fn.RequireArity("Nz", a.Count, 1, 2, span, d);
        if (a.Count >= 1) Fn.RequireParam("Nz", 0, P.Number, a[0], span, d);
        if (a.Count == 2) Fn.RequireParam("Nz", 1, P.Number, a[1], span, d);
        return FormulaType.Number;
    }

    // Case(test, match1, result1, match2, result2, ..., [default])
    private static FormulaType CheckCase(IReadOnlyList<FormulaType> a, TextSpan span, List<FormulaDiagnostic> d)
    {
        Fn.RequireArity("Case", a.Count, 3, int.MaxValue, span, d);
        int n = a.Count;
        if (n < 3) return n > 2 ? a[2] : FormulaType.Null;

        bool hasDefault = (n - 1) % 2 == 1;
        int pairEnd = hasDefault ? n - 1 : n;
        var result = FormulaType.Null;
        for (int i = 1; i + 1 < pairEnd; i += 2) result = Fn.CommonType(result, a[i + 1]);
        if (hasDefault) result = Fn.CommonType(result, a[n - 1]);
        return result == FormulaType.Null && n > 2 ? a[2] : result;
    }

    private static FormulaValue EvalCase(IReadOnlyList<Func<FormulaValue>> t, EvaluationOptions opt)
    {
        var test = t[0]();
        int n = t.Count;
        bool hasDefault = (n - 1) % 2 == 1;
        int pairEnd = hasDefault ? n - 1 : n;
        for (int i = 1; i + 1 < pairEnd; i += 2)
            if (ValueOps.AreEqual(test, t[i]())) return t[i + 1]();
        return hasDefault ? t[n - 1]() : FormulaValue.Null(FormulaType.Null);
    }
}
