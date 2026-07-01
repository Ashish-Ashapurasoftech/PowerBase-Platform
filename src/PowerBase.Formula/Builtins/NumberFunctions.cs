using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

internal static class NumberFunctions
{
    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.Exact("Abs", FormulaType.Number, new[] { P.Number }, (a, _) => N1(a[0], x => Math.Abs(x))));
        r.Add(Fn.Exact("Int", FormulaType.Number, new[] { P.Number }, (a, _) => N1(a[0], x => Math.Truncate(x))));
        r.Add(Fn.Exact("Sqrt", FormulaType.Number, new[] { P.Number },
            (a, _) => a[0].IsNull || a[0].AsNumber() < 0 ? NullNum : FormulaValue.Number((decimal)Math.Sqrt((double)a[0].AsNumber()))));
        r.Add(Fn.Exact("Mod", FormulaType.Number, new[] { P.Number, P.Number },
            (a, _) => a[0].IsNull || a[1].IsNull || a[1].AsNumber() == 0 ? NullNum : FormulaValue.Number(a[0].AsNumber() % a[1].AsNumber())));
        r.Add(Fn.Exact("Rem", FormulaType.Number, new[] { P.Number, P.Number },
            (a, _) => a[0].IsNull || a[1].IsNull || a[1].AsNumber() == 0 ? NullNum : FormulaValue.Number(a[0].AsNumber() % a[1].AsNumber())));
        r.Add(Fn.Range("Ceil", FormulaType.Number, new[] { P.Number }, new[] { P.Number },
            (a, _) => a[0].IsNull ? NullNum : FormulaValue.Number(ToMultiple(a[0].AsNumber(), Multiple(a), up: true))));
        r.Add(Fn.Range("Floor", FormulaType.Number, new[] { P.Number }, new[] { P.Number },
            (a, _) => a[0].IsNull ? NullNum : FormulaValue.Number(ToMultiple(a[0].AsNumber(), Multiple(a), up: false))));
        r.Add(Fn.Range("Round", FormulaType.Number, new[] { P.Number }, new[] { P.Number },
            (a, _) => a[0].IsNull ? NullNum : FormulaValue.Number(RoundHalfUp(a[0].AsNumber(), Digits(a)))));
        r.Add(Fn.Range("RoundDown", FormulaType.Number, new[] { P.Number }, new[] { P.Number },
            (a, _) => a[0].IsNull ? NullNum : FormulaValue.Number(RoundDir(a[0].AsNumber(), Digits(a), up: false))));
        r.Add(Fn.Range("RoundUp", FormulaType.Number, new[] { P.Number }, new[] { P.Number },
            (a, _) => a[0].IsNull ? NullNum : FormulaValue.Number(RoundDir(a[0].AsNumber(), Digits(a), up: true))));
        r.Add(Fn.Variadic("Sum", FormulaType.Number, P.Number, 1, (a, _) => FormulaValue.Number(NonNull(a).Sum())));
        r.Add(Fn.Variadic("Min", FormulaType.Number, P.Number, 1, (a, _) => Agg(a, v => v.Min())));
        r.Add(Fn.Variadic("Max", FormulaType.Number, P.Number, 1, (a, _) => Agg(a, v => v.Max())));
        r.Add(Fn.Variadic("Average", FormulaType.Number, P.Number, 1, (a, _) => Agg(a, v => v.Average())));
        r.Add(Fn.Exact("ToNumber", FormulaType.Number, new[] { P.Any }, (a, _) => ValueConvert.ToNumberValue(a[0])));
    }

    private static readonly FormulaValue NullNum = FormulaValue.Null(FormulaType.Number);

    private static FormulaValue N1(FormulaValue v, Func<decimal, decimal> f) => v.IsNull ? NullNum : FormulaValue.Number(f(v.AsNumber()));

    private static IEnumerable<decimal> NonNull(IReadOnlyList<FormulaValue> a) => a.Where(v => !v.IsNull).Select(v => v.AsNumber());

    private static FormulaValue Agg(IReadOnlyList<FormulaValue> a, Func<IReadOnlyList<decimal>, decimal> f)
    {
        var values = NonNull(a).ToList();
        return values.Count == 0 ? NullNum : FormulaValue.Number(f(values));
    }

    private static int Digits(IReadOnlyList<FormulaValue> a) => a.Count == 2 && !a[1].IsNull ? (int)Math.Truncate(a[1].AsNumber()) : 0;

    // Ceil/Floor round to the nearest multiple (default 1). A non-positive multiple is a no-op.
    private static decimal Multiple(IReadOnlyList<FormulaValue> a) => a.Count == 2 && !a[1].IsNull ? a[1].AsNumber() : 1m;

    private static decimal ToMultiple(decimal x, decimal multiple, bool up)
    {
        if (multiple <= 0) return x;
        var q = x / multiple;
        return (up ? Math.Ceiling(q) : Math.Floor(q)) * multiple;
    }

    private static decimal Pow10(int e)
    {
        decimal r = 1m;
        if (e >= 0) for (int i = 0; i < e; i++) r *= 10m;
        else for (int i = 0; i > e; i--) r /= 10m;
        return r;
    }

    // Quickbase rounds halves up (toward +∞): -3.5 → -3.
    private static decimal RoundHalfUp(decimal x, int digits)
    {
        var f = Pow10(digits);
        return Math.Floor(x * f + 0.5m) / f;
    }

    private static decimal RoundDir(decimal x, int digits, bool up)
    {
        var f = Pow10(digits);
        var y = x * f;
        return (up ? Math.Ceiling(y) : Math.Floor(y)) / f;
    }
}
