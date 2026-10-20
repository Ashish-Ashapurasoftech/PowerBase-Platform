using System.Globalization;
using System.Text;

namespace PowerBase.Application.Imports.Files;

/// <summary>What a date or time cell of a workbook shows: the cell holds a number (days since 1900), and its number format says how that is written on the sheet
/// (<c>dd/mm/yyyy</c>, <c>mm-dd-yyyy</c>, <c>hh:mm AM/PM</c>...). A person looking at the sheet sees the text, so the import reads that text in the format they chose for
/// the column, exactly as it reads a date typed as text. A format this cannot draw returns null and the cell is read from its value.</summary>
public static class ImportExcelDisplay
{
    private static readonly Dictionary<uint, string> BuiltIn = new()
    {
        [14] = "m/d/yyyy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss",
        [22] = "m/d/yyyy h:mm", [45] = "mm:ss", [47] = "mm:ss.0",
    };

    /// <summary>The format code of a built-in number format id, or null when the id is not one of the date and time formats whose code does not depend on the machine.</summary>
    public static string? BuiltInCode(uint id) => BuiltIn.GetValueOrDefault(id);

    /// <summary>The cell's value as its number format writes it, or null when the format is one this does not draw (elapsed time, text sections, locale codes it cannot read).</summary>
    public static string? Render(double serial, string formatCode)
    {
        if (serial < 0 || serial >= 2_958_466) return null;
        DateTime value;
        try { value = DateTime.FromOADate(Math.Round(serial * 86400) / 86400); }
        catch (ArgumentException) { return null; }

        // Only the first section (positive numbers) writes a date.
        var code = FirstSection(formatCode);
        var tokens = Tokenize(code);
        if (tokens is null) return null;
        var twelveHour = tokens.Any(t => t.Kind == Kind.Marker);

        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            switch (t.Kind)
            {
                case Kind.Literal: sb.Append(t.Text); break;
                case Kind.Year: sb.Append(t.Text.Length >= 3 ? value.Year.ToString("0000", CultureInfo.InvariantCulture) : (value.Year % 100).ToString("00", CultureInfo.InvariantCulture)); break;
                case Kind.Month: sb.Append(Month(value, t.Text.Length)); break;
                case Kind.Minute: sb.Append(t.Text.Length >= 2 ? value.Minute.ToString("00", CultureInfo.InvariantCulture) : value.Minute.ToString(CultureInfo.InvariantCulture)); break;
                case Kind.Day:
                    sb.Append(t.Text.Length switch
                    {
                        >= 4 => value.DayOfWeek.ToString(),
                        3 => value.DayOfWeek.ToString()[..3],
                        2 => value.Day.ToString("00", CultureInfo.InvariantCulture),
                        _ => value.Day.ToString(CultureInfo.InvariantCulture)
                    });
                    break;
                case Kind.Hour:
                {
                    var hour = twelveHour ? (value.Hour % 12 == 0 ? 12 : value.Hour % 12) : value.Hour;
                    sb.Append(t.Text.Length >= 2 ? hour.ToString("00", CultureInfo.InvariantCulture) : hour.ToString(CultureInfo.InvariantCulture));
                    break;
                }
                case Kind.Second: sb.Append(t.Text.Length >= 2 ? value.Second.ToString("00", CultureInfo.InvariantCulture) : value.Second.ToString(CultureInfo.InvariantCulture)); break;
                case Kind.Marker: sb.Append(t.Text.Length > 1 && t.Text.Contains('/') && t.Text.Length == 3 ? (value.Hour < 12 ? "A" : "P") : value.Hour < 12 ? "AM" : "PM"); break;
            }
        }
        return sb.ToString();
    }

    private enum Kind { Literal, Year, Month, Minute, Day, Hour, Second, Marker }

    private readonly record struct Token(Kind Kind, string Text);

    private static string FirstSection(string code)
    {
        var inQuote = false;
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] == '"') inQuote = !inQuote;
            else if (code[i] == '\\') i++;
            else if (code[i] == ';' && !inQuote) return code[..i];
        }
        return code;
    }

    /// <summary>Splits a format code into what is written literally and the date and time codes. An "m" is a minute when it follows an hour or comes before seconds.</summary>
    private static List<Token>? Tokenize(string code)
    {
        var tokens = new List<Token>();
        var lastCode = Kind.Literal;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            switch (c)
            {
                case '\\': if (i + 1 < code.Length) tokens.Add(new Token(Kind.Literal, code[++i].ToString())); break;
                case '"':
                {
                    var end = code.IndexOf('"', i + 1);
                    if (end < 0) return null;
                    tokens.Add(new Token(Kind.Literal, code[(i + 1)..end]));
                    i = end;
                    break;
                }
                case '[':
                {
                    var end = code.IndexOf(']', i + 1);
                    if (end < 0) return null;
                    var inner = code[(i + 1)..end];
                    if (inner.Length > 0 && char.ToLowerInvariant(inner[0]) is 'h' or 'm' or 's') return null; // elapsed time: not a clock reading
                    i = end; // a colour or a locale code
                    break;
                }
                case '_': i++; break;
                case '*': i++; break;
                case 'G' or 'g' when string.Compare(code, i, "General", 0, 7, StringComparison.OrdinalIgnoreCase) == 0: return null;
                case 'A' or 'a':
                {
                    if (string.Compare(code, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) { tokens.Add(new Token(Kind.Marker, "AM/PM")); i += 4; }
                    else if (string.Compare(code, i, "A/P", 0, 3, StringComparison.OrdinalIgnoreCase) == 0) { tokens.Add(new Token(Kind.Marker, "A/P")); i += 2; }
                    else tokens.Add(new Token(Kind.Literal, c.ToString()));
                    break;
                }
                case 'y' or 'Y': i = Run(code, i, c, out var y); tokens.Add(new Token(Kind.Year, y)); lastCode = Kind.Year; break;
                case 'd' or 'D': i = Run(code, i, c, out var d); tokens.Add(new Token(Kind.Day, d)); lastCode = Kind.Day; break;
                case 'h' or 'H': i = Run(code, i, c, out var h); tokens.Add(new Token(Kind.Hour, h)); lastCode = Kind.Hour; break;
                case 's' or 'S':
                    i = Run(code, i, c, out var s);
                    tokens.Add(new Token(Kind.Second, s));
                    lastCode = Kind.Second;
                    // "mm:ss" with no hour: the m before is a minute
                    for (var k = tokens.Count - 2; k >= 0; k--)
                    {
                        if (tokens[k].Kind == Kind.Literal) continue;
                        if (tokens[k].Kind == Kind.Month) tokens[k] = new Token(Kind.Minute, tokens[k].Text);
                        break;
                    }
                    break;
                case 'm' or 'M':
                    i = Run(code, i, c, out var m);
                    tokens.Add(new Token(lastCode == Kind.Hour ? Kind.Minute : Kind.Month, m));
                    if (lastCode != Kind.Hour) lastCode = Kind.Month;
                    break;
                case '.' when i + 1 < code.Length && code[i + 1] == '0': // fractions of a second are not shown
                    while (i + 1 < code.Length && code[i + 1] == '0') i++;
                    break;
                default: tokens.Add(new Token(Kind.Literal, c.ToString())); break;
            }
        }
        return tokens.Any(t => t.Kind != Kind.Literal) ? tokens : null;
    }

    private static int Run(string code, int start, char c, out string run)
    {
        var end = start;
        while (end + 1 < code.Length && char.ToLowerInvariant(code[end + 1]) == char.ToLowerInvariant(c)) end++;
        run = code[start..(end + 1)];
        return end;
    }

    private static string Month(DateTime value, int length)
    {
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        return length switch
        {
            >= 5 => names.GetMonthName(value.Month)[..1],
            4 => names.GetMonthName(value.Month),
            3 => names.GetAbbreviatedMonthName(value.Month),
            2 => value.Month.ToString("00", CultureInfo.InvariantCulture),
            _ => value.Month.ToString(CultureInfo.InvariantCulture)
        };
    }
}
