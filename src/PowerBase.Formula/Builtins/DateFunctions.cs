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

        // ── Wave 1: register-only date/time additions ──
        r.Add(Fn.Exact("DayOfYear", FormulaType.Number, new[] { P.DateLike }, (a, _) => Part(a[0], d => d.DayOfYear)));
        r.Add(Fn.Exact("Hour", FormulaType.Number, new[] { P.DateLike }, (a, _) => TimePart(a[0], t => t.Hour)));
        r.Add(Fn.Exact("Minute", FormulaType.Number, new[] { P.DateLike }, (a, _) => TimePart(a[0], t => t.Minute)));
        r.Add(Fn.Exact("Second", FormulaType.Number, new[] { P.DateLike }, (a, _) => TimePart(a[0], t => t.Second)));

        r.Add(Fn.Exact("NameOfDay", FormulaType.Text, new[] { P.DateLike }, (a, _) => Name(a[0], d => Inv.GetDayName(d.DayOfWeek))));
        r.Add(Fn.Exact("NameOfMonth", FormulaType.Text, new[] { P.DateLike }, (a, _) => Name(a[0], d => Inv.GetMonthName(d.Month))));

        r.Add(Fn.Exact("FirstDayOfMonth", FormulaType.Date, new[] { P.DateLike }, (a, _) => Map(a[0], d => new DateOnly(d.Year, d.Month, 1))));
        r.Add(Fn.Exact("LastDayOfMonth", FormulaType.Date, new[] { P.DateLike }, (a, _) => Map(a[0], d => new DateOnly(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month)))));
        r.Add(Fn.Exact("FirstDayOfWeek", FormulaType.Date, new[] { P.DateLike }, (a, _) => Map(a[0], d => d.AddDays(-(int)d.DayOfWeek))));
        r.Add(Fn.Exact("LastDayOfWeek", FormulaType.Date, new[] { P.DateLike }, (a, _) => Map(a[0], d => d.AddDays(6 - (int)d.DayOfWeek))));
        r.Add(Fn.Exact("FirstDayOfYear", FormulaType.Date, new[] { P.DateLike }, (a, _) => Map(a[0], d => new DateOnly(d.Year, 1, 1))));
        r.Add(Fn.Exact("LastDayOfYear", FormulaType.Date, new[] { P.DateLike }, (a, _) => Map(a[0], d => new DateOnly(d.Year, 12, 31))));

        r.Add(Fn.Exact("NextDayOfWeek", FormulaType.Date, new[] { P.DateLike, P.Number }, (a, _) => NearestDayOfWeek(a, forward: true)));
        r.Add(Fn.Exact("PrevDayOfWeek", FormulaType.Date, new[] { P.DateLike, P.Number }, (a, _) => NearestDayOfWeek(a, forward: false)));

        r.Add(Fn.Exact("IsWeekday", FormulaType.Bool, new[] { P.DateLike },
            (a, _) => { var d = AsDateOnly(a[0]); return d is null ? FormulaValue.Bool(false) : FormulaValue.Bool(IsWeekdayDate(d.Value)); }));
        r.Add(Fn.Exact("WeekdayAdd", FormulaType.Date, new[] { P.DateLike, P.Number }, (a, _) => WeekdayAdd(a[0], ToInt(a[1]))));
        r.Add(Fn.Exact("WeekdaySub", FormulaType.Date, new[] { P.DateLike, P.Number }, (a, _) => WeekdayAdd(a[0], -ToInt(a[1]))));

        r.Add(Fn.Exact("ToHours", FormulaType.Number, new[] { P.Duration }, (a, _) => DurNum(a[0], t => (decimal)t.TotalHours)));
        r.Add(Fn.Exact("ToMinutes", FormulaType.Number, new[] { P.Duration }, (a, _) => DurNum(a[0], t => (decimal)t.TotalMinutes)));
        r.Add(Fn.Exact("ToSeconds", FormulaType.Number, new[] { P.Duration }, (a, _) => DurNum(a[0], t => (decimal)t.TotalSeconds)));
        r.Add(Fn.Exact("ToWeeks", FormulaType.Number, new[] { P.Duration }, (a, _) => DurNum(a[0], t => (decimal)t.TotalDays / 7)));

        r.Add(Fn.Exact("ToTimestamp", FormulaType.DateTime, new[] { P.Any }, (a, _) => ValueConvert.ToDateTimeValue(a[0])));
    }

    private static readonly System.Globalization.DateTimeFormatInfo Inv = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat;

    private static FormulaValue TimePart(FormulaValue v, Func<DateTime, int> f)
    {
        var dt = AsDateTimeOrNull(v);
        return dt is null ? FormulaValue.Null(FormulaType.Number) : FormulaValue.Number(f(dt.Value));
    }

    private static DateTime? AsDateTimeOrNull(FormulaValue v) => v.IsNull ? null
        : v.Type == FormulaType.DateTime ? v.AsDateTime()
        : v.Type == FormulaType.Date ? v.AsDate().ToDateTime(TimeOnly.MinValue)
        : null;

    private static FormulaValue Name(FormulaValue v, Func<DateOnly, string> f)
    {
        var d = AsDateOnly(v);
        return d is null ? FormulaValue.Text(string.Empty) : FormulaValue.Text(f(d.Value));
    }

    private static FormulaValue Map(FormulaValue v, Func<DateOnly, DateOnly> f)
    {
        var d = AsDateOnly(v);
        return d is null ? NullDate : FormulaValue.Date(f(d.Value));
    }

    private static FormulaValue DurNum(FormulaValue v, Func<TimeSpan, decimal> f)
        => v.IsNull ? FormulaValue.Null(FormulaType.Number) : FormulaValue.Number(f(v.AsDuration()));

    private static bool IsWeekdayDate(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    // weekday param is 1–7 with Sunday = 1 (matches WeekDay()/DayOfWeek()).
    private static DayOfWeek? Weekday(FormulaValue v)
    {
        if (v.IsNull) return null;
        int n = ToInt(v);
        return n is >= 1 and <= 7 ? (DayOfWeek)((n - 1) % 7) : null;
    }

    private static FormulaValue NearestDayOfWeek(IReadOnlyList<FormulaValue> a, bool forward)
    {
        var d = AsDateOnly(a[0]);
        var target = Weekday(a[1]);
        if (d is null || target is null) return NullDate;
        for (int i = 1; i <= 7; i++)
        {
            var cand = d.Value.AddDays(forward ? i : -i);
            if (cand.DayOfWeek == target.Value) return FormulaValue.Date(cand);
        }
        return NullDate;
    }

    private static FormulaValue WeekdayAdd(FormulaValue v, int n)
    {
        var d = AsDateOnly(v);
        if (d is null) return NullDate;
        var cur = d.Value;
        int step = n >= 0 ? 1 : -1;
        int remaining = Math.Abs(n);
        while (remaining > 0)
        {
            cur = cur.AddDays(step);
            if (IsWeekdayDate(cur)) remaining--;
        }
        return FormulaValue.Date(cur);
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
