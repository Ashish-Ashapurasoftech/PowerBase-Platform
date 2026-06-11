using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

internal static class DateFunctions
{
    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.Nullary("Today", FormulaType.Date, opt => FormulaValue.Date(opt.Today)));
        r.Add(Fn.Nullary("Now", FormulaType.DateTime, opt => FormulaValue.DateTime(opt.UtcNow)));

        r.Add(Fn.Exact("Year", FormulaType.Number, new[] { P.DateLike }, (a, _) => Part(a[0], d => d.Year)));
        r.Add(Fn.Exact("Month", FormulaType.Number, new[] { P.DateLike }, (a, _) => Part(a[0], d => d.Month)));
        r.Add(Fn.Exact("Day", FormulaType.Number, new[] { P.DateLike }, (a, _) => Part(a[0], d => d.Day)));
        r.Add(Fn.Exact("Quarter", FormulaType.Number, new[] { P.DateLike }, (a, _) => Part(a[0], d => (d.Month - 1) / 3 + 1)));
        r.Add(Fn.Exact("WeekDay", FormulaType.Number, new[] { P.DateLike }, (a, _) => Part(a[0], d => (int)d.DayOfWeek + 1)));
        r.Add(Fn.Exact("DayOfWeek", FormulaType.Number, new[] { P.DateLike }, (a, _) => Part(a[0], d => (int)d.DayOfWeek + 1)));

        r.Add(Fn.Exact("Date", FormulaType.Date, new[] { P.Number, P.Number, P.Number }, (a, _) => MakeDate(a)));
        r.Add(Fn.Exact("ToDate", FormulaType.Date, new[] { P.Any }, (a, _) => ValueConvert.ToDateValue(a[0])));
        r.Add(Fn.Exact("AdjustMonth", FormulaType.Date, new[] { P.Date, P.Number },
            (a, _) => a[0].IsNull || a[1].IsNull ? NullDate : FormulaValue.Date(a[0].AsDate().AddMonths(ToInt(a[1])))));
        r.Add(Fn.Exact("AdjustYear", FormulaType.Date, new[] { P.Date, P.Number },
            (a, _) => a[0].IsNull || a[1].IsNull ? NullDate : FormulaValue.Date(a[0].AsDate().AddYears(ToInt(a[1])))));

        r.Add(Fn.Exact("ToDays", FormulaType.Number, new[] { P.Duration },
            (a, _) => a[0].IsNull ? FormulaValue.Null(FormulaType.Number) : FormulaValue.Number((decimal)a[0].AsDuration().TotalDays)));
        r.Add(Fn.Exact("Days", FormulaType.Duration, new[] { P.Number }, (a, _) => Dur(a[0], n => TimeSpan.FromDays((double)n))));
        r.Add(Fn.Exact("Hours", FormulaType.Duration, new[] { P.Number }, (a, _) => Dur(a[0], n => TimeSpan.FromHours((double)n))));
        r.Add(Fn.Exact("Minutes", FormulaType.Duration, new[] { P.Number }, (a, _) => Dur(a[0], n => TimeSpan.FromMinutes((double)n))));
        r.Add(Fn.Exact("Seconds", FormulaType.Duration, new[] { P.Number }, (a, _) => Dur(a[0], n => TimeSpan.FromSeconds((double)n))));
        r.Add(Fn.Exact("Weeks", FormulaType.Duration, new[] { P.Number }, (a, _) => Dur(a[0], n => TimeSpan.FromDays((double)n * 7))));

        r.Add(Fn.Exact("DateAdd", FormulaType.Date, new[] { P.Date, P.Text, P.Number }, (a, _) => DateAdd(a)));
        r.Add(Fn.Range("DateDiff", FormulaType.Number, new[] { P.Date, P.Date }, new[] { P.Text }, (a, _) => DateDiff(a)));
    }

    private static readonly FormulaValue NullDate = FormulaValue.Null(FormulaType.Date);

    private static int ToInt(FormulaValue v) => v.IsNull ? 0 : (int)Math.Truncate(v.AsNumber());

    private static DateOnly? AsDateOnly(FormulaValue v) => v.IsNull ? null
        : v.Type == FormulaType.Date ? v.AsDate()
        : v.Type == FormulaType.DateTime ? DateOnly.FromDateTime(v.AsDateTime())
        : null;

    private static FormulaValue Part(FormulaValue v, Func<DateOnly, int> f)
    {
        var d = AsDateOnly(v);
        return d is null ? FormulaValue.Null(FormulaType.Number) : FormulaValue.Number(f(d.Value));
    }

    private static FormulaValue Dur(FormulaValue v, Func<decimal, TimeSpan> f)
        => v.IsNull ? FormulaValue.Null(FormulaType.Duration) : FormulaValue.Duration(f(v.AsNumber()));

    private static FormulaValue MakeDate(IReadOnlyList<FormulaValue> a)
    {
        if (a[0].IsNull || a[1].IsNull || a[2].IsNull) return NullDate;
        try { return FormulaValue.Date(new DateOnly(ToInt(a[0]), ToInt(a[1]), ToInt(a[2]))); }
        catch (ArgumentOutOfRangeException) { return NullDate; }
    }

    private static FormulaValue DateAdd(IReadOnlyList<FormulaValue> a)
    {
        if (a[0].IsNull || a[2].IsNull) return NullDate;
        var d = a[0].AsDate();
        int n = ToInt(a[2]);
        return a[1].AsText().ToLowerInvariant() switch
        {
            "day" or "days" => FormulaValue.Date(d.AddDays(n)),
            "week" or "weeks" => FormulaValue.Date(d.AddDays(n * 7)),
            "month" or "months" => FormulaValue.Date(d.AddMonths(n)),
            "year" or "years" => FormulaValue.Date(d.AddYears(n)),
            _ => NullDate,
        };
    }

    private static FormulaValue DateDiff(IReadOnlyList<FormulaValue> a)
    {
        if (a[0].IsNull || a[1].IsNull) return FormulaValue.Null(FormulaType.Number);
        int days = a[0].AsDate().DayNumber - a[1].AsDate().DayNumber;
        var unit = a.Count == 3 && !a[2].IsNull ? a[2].AsText().ToLowerInvariant() : "days";
        return unit switch
        {
            "week" or "weeks" => FormulaValue.Number((decimal)days / 7),
            _ => FormulaValue.Number(days),
        };
    }
}
