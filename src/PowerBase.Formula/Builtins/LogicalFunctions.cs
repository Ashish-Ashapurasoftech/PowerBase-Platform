using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

internal static class LogicalFunctions
{
    public static void Register(FunctionRegistry r)
    {
        // If(condition, value) — and Quickbase's multi-branch form If(c1, v1, c2, v2, …, [else]). Conditions are tried in order and the first true
        // one gives its value; a final odd argument is the else value, and without one the result is blank. A 3-argument call is the classic
        // If(condition, then, else).
        r.Add(Fn.Lazy("If", CheckIf, EvalIf));

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
        Fn.RequireArity("If", a.Count, 2, int.MaxValue, span, d);
        var result = FormulaType.Null;
        var first = FormulaType.Null;
        var mismatch = false;
        for (var i = 0; i < a.Count; i++)
        {
            var isCondition = i % 2 == 0 && i < a.Count - (a.Count % 2 == 1 ? 1 : 0);
            if (isCondition)
            {
                Fn.RequireParam("If", i, P.Bool, a[i], span, d);
                continue;
            }

            if (a[i] == FormulaType.Null) continue;
            if (first == FormulaType.Null) first = a[i];
            var common = Fn.CommonType(result, a[i]);
            if (common == FormulaType.Null && result != FormulaType.Null)
            {
                d.Add(new FormulaDiagnostic(FormulaErrorCode.TypeMismatch, $"If branches must have the same type (got {result} and {a[i]}).", span));
                mismatch = true;
                break;
            }
            result = common;
        }
        return mismatch ? first : result;
    }

    private static FormulaValue EvalIf(IReadOnlyList<Func<FormulaValue>> t, EvaluationOptions opt)
    {
        var pairs = t.Count / 2;
        for (var i = 0; i < pairs; i++)
            if (t[2 * i]().AsBool()) return t[2 * i + 1]();
        return t.Count % 2 == 1 ? t[^1]() : FormulaValue.Null(FormulaType.Null);
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
