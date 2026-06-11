using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

internal delegate FormulaValue EagerImpl(IReadOnlyList<FormulaValue> args, EvaluationOptions options);

/// <summary>
/// Declarative builders for <see cref="FormulaFunction"/>s, plus the shared
/// arity/parameter/result-type checking helpers. Keeps each function definition to
/// a single readable line in the category files.
/// </summary>
internal static class Fn
{
    /// <summary>Fixed arity; every position validated against <paramref name="ps"/>.</summary>
    public static DelegateFunction Exact(string name, FormulaType ret, Param[] ps, EagerImpl impl) =>
        new(name,
            (args, span, d) =>
            {
                RequireArity(name, args.Count, ps.Length, ps.Length, span, d);
                for (int i = 0; i < Math.Min(args.Count, ps.Length); i++) RequireParam(name, i, ps[i], args[i], span, d);
                return ret;
            },
            Eager(impl));

    /// <summary>Arity in [required, required+optional]; positions validated in order.</summary>
    public static DelegateFunction Range(string name, FormulaType ret, Param[] required, Param[] optional, EagerImpl impl) =>
        new(name,
            (args, span, d) =>
            {
                RequireArity(name, args.Count, required.Length, required.Length + optional.Length, span, d);
                for (int i = 0; i < args.Count; i++)
                {
                    if (i < required.Length) RequireParam(name, i, required[i], args[i], span, d);
                    else if (i - required.Length < optional.Length) RequireParam(name, i, optional[i - required.Length], args[i], span, d);
                }
                return ret;
            },
            Eager(impl));

    /// <summary>Any number (≥ <paramref name="min"/>) of arguments, all of <paramref name="elem"/>.</summary>
    public static DelegateFunction Variadic(string name, FormulaType ret, Param elem, int min, EagerImpl impl) =>
        new(name,
            (args, span, d) =>
            {
                RequireArity(name, args.Count, min, int.MaxValue, span, d);
                for (int i = 0; i < args.Count; i++) RequireParam(name, i, elem, args[i], span, d);
                return ret;
            },
            Eager(impl));

    /// <summary>Zero-argument function (e.g. Today, Now, True).</summary>
    public static DelegateFunction Nullary(string name, FormulaType ret, Func<EvaluationOptions, FormulaValue> impl) =>
        new(name,
            (args, span, d) => { RequireArity(name, args.Count, 0, 0, span, d); return ret; },
            (_, opt) => impl(opt));

    /// <summary>Full control over both type-checking and (lazy) evaluation — for If/Case/Nz.</summary>
    public static DelegateFunction Lazy(string name, FunctionTypeCheck check, FunctionEval eval) => new(name, check, eval);

    // ── Shared checking helpers (used by Lazy functions too) ─────────────────

    public static void RequireArity(string name, int count, int min, int max, TextSpan span, List<FormulaDiagnostic> d)
    {
        if (count >= min && count <= max) return;
        var expected = min == max ? min.ToString() : max == int.MaxValue ? $"at least {min}" : $"{min}–{max}";
        d.Add(new FormulaDiagnostic(FormulaErrorCode.WrongArgumentCount, $"{name} expects {expected} argument(s) but got {count}.", span));
    }

    public static void RequireParam(string name, int index, Param p, FormulaType actual, TextSpan span, List<FormulaDiagnostic> d)
    {
        if (actual == FormulaType.Null) return; // operand already errored — don't pile on
        if (!p.Accepts(actual))
            d.Add(new FormulaDiagnostic(FormulaErrorCode.TypeMismatch, $"{name} argument {index + 1} expects {p.Desc} but got {actual}.", span));
    }

    /// <summary>The shared type of two branches (If/Case results): equal types, or the non-null one.</summary>
    public static FormulaType CommonType(FormulaType a, FormulaType b)
    {
        if (a == b) return a;
        if (a == FormulaType.Null) return b;
        if (b == FormulaType.Null) return a;
        return FormulaType.Null; // incompatible — caller reports the mismatch
    }

    private static FunctionEval Eager(EagerImpl impl) => (thunks, opt) =>
    {
        var values = new FormulaValue[thunks.Count];
        for (int i = 0; i < thunks.Count; i++) values[i] = thunks[i]();
        return impl(values, opt);
    };
}
