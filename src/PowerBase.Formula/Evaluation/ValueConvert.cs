using System.Globalization;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Evaluation;

/// <summary>
/// Conversions between raw stored values and <see cref="FormulaValue"/>, and the
/// explicit type conversions used by the <c>To*</c> functions. Runtime conversion
/// failures fail soft (return a typed null) so a single bad cell never breaks a
/// record read. Duration values are stored/exposed as minutes.
/// </summary>
internal static class ValueConvert
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string NumberFormat = "0.###############";

    /// <summary>Coerce a raw stored value (from the record context) into the field's declared type.</summary>
    public static FormulaValue FromRaw(FormulaType type, object? raw)
    {
        if (raw is null or DBNull) return FormulaValue.Null(type);

        return type switch
        {
            FormulaType.Text => FormulaValue.Text(RawToString(raw)),
            FormulaType.Number => TryToDecimal(raw, out var d) ? FormulaValue.Number(d) : FormulaValue.Null(FormulaType.Number),
            FormulaType.Bool => FormulaValue.Bool(RawToBool(raw)),
            FormulaType.Date => TryToDateOnly(raw, out var dt) ? FormulaValue.Date(dt) : FormulaValue.Null(FormulaType.Date),
            FormulaType.DateTime => TryToDateTime(raw, out var dtt) ? FormulaValue.DateTime(dtt) : FormulaValue.Null(FormulaType.DateTime),
            FormulaType.Duration => TryToDecimal(raw, out var m) ? FormulaValue.Duration(TimeSpan.FromMinutes((double)m)) : FormulaValue.Null(FormulaType.Duration),
            FormulaType.User => FormulaValue.User(new UserRef(RawToString(raw))),
            FormulaType.UserList => FormulaValue.UserList(ParseUserList(raw)),
            _ => FormulaValue.Null(type),
        };
    }

    // ── Explicit conversions (To* functions) ─────────────────────────────────

    public static string ToTextString(FormulaValue v) => v.IsNull ? string.Empty : v.Type switch
    {
        FormulaType.Text => v.AsText(),
        FormulaType.Number => v.AsNumber().ToString(NumberFormat, CultureInfo.InvariantCulture),
        FormulaType.Bool => v.AsBool() ? "true" : "false",
        FormulaType.Date => v.AsDate().ToString(DateFormat, CultureInfo.InvariantCulture),
        FormulaType.DateTime => v.AsDateTime().ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        FormulaType.Duration => ((decimal)v.AsDuration().TotalMinutes).ToString(NumberFormat, CultureInfo.InvariantCulture),
        FormulaType.User => v.AsUser().Email ?? v.AsUser().UserId,
        FormulaType.UserList => string.Join(";", v.AsUserList().Select(u => u.Email ?? u.UserId)),
        _ => string.Empty,
    };

    public static FormulaValue ToNumberValue(FormulaValue v)
    {
        if (v.IsNull) return FormulaValue.Null(FormulaType.Number);
        switch (v.Type)
        {
            case FormulaType.Number: return v;
            case FormulaType.Bool: return FormulaValue.Number(v.AsBool() ? 1 : 0);
            case FormulaType.Duration: return FormulaValue.Number((decimal)v.AsDuration().TotalMinutes);
            case FormulaType.Text:
                return decimal.TryParse(v.AsText(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                    ? FormulaValue.Number(d)
                    : FormulaValue.Null(FormulaType.Number);
            default: return FormulaValue.Null(FormulaType.Number);
        }
    }

    public static FormulaValue ToDateValue(FormulaValue v)
    {
        if (v.IsNull) return FormulaValue.Null(FormulaType.Date);
        switch (v.Type)
        {
            case FormulaType.Date: return v;
            case FormulaType.DateTime: return FormulaValue.Date(DateOnly.FromDateTime(v.AsDateTime()));
            case FormulaType.Text:
                return DateOnly.TryParse(v.AsText(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? FormulaValue.Date(dt)
                    : FormulaValue.Null(FormulaType.Date);
            default: return FormulaValue.Null(FormulaType.Date);
        }
    }

    public static FormulaValue ToBoolValue(FormulaValue v)
    {
        if (v.IsNull) return FormulaValue.Bool(false);
        return v.Type switch
        {
            FormulaType.Bool => v,
            FormulaType.Number => FormulaValue.Bool(v.AsNumber() != 0),
            FormulaType.Text => FormulaValue.Bool(IsTruthy(v.AsText())),
            _ => FormulaValue.Bool(false),
        };
    }

    private static bool IsTruthy(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";

    // ── Raw helpers ──────────────────────────────────────────────────────────

    private static string RawToString(object raw) => raw switch
    {
        string s => s,
        DateTime dt => dt.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(DateFormat, CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => raw.ToString() ?? string.Empty,
    };

    private static bool RawToBool(object raw) => raw switch
    {
        bool b => b,
        string s => IsTruthy(s),
        IConvertible => TryToDecimal(raw, out var d) && d != 0,
        _ => false,
    };

    private static bool TryToDecimal(object raw, out decimal value)
    {
        switch (raw)
        {
            case decimal d: value = d; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case short sh: value = sh; return true;
            case byte by: value = by; return true;
            case double db: value = (decimal)db; return true;
            case float f: value = (decimal)f; return true;
            case bool b: value = b ? 1 : 0; return true;
            case string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                value = parsed; return true;
            default: value = 0; return false;
        }
    }

    private static bool TryToDateOnly(object raw, out DateOnly value)
    {
        switch (raw)
        {
            case DateOnly d: value = d; return true;
            case DateTime dt: value = DateOnly.FromDateTime(dt); return true;
            case string s when DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p):
                value = p; return true;
            case string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pdt):
                value = DateOnly.FromDateTime(pdt); return true;
            default: value = default; return false;
        }
    }

    private static bool TryToDateTime(object raw, out DateTime value)
    {
        switch (raw)
        {
            case DateTime dt: value = dt; return true;
            case DateOnly d: value = d.ToDateTime(TimeOnly.MinValue); return true;
            case string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p):
                value = p; return true;
            default: value = default; return false;
        }
    }

    private static IReadOnlyList<UserRef> ParseUserList(object raw)
    {
        var s = RawToString(raw);
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<UserRef>();
        return s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => new UserRef(part))
                .ToList();
    }
}
