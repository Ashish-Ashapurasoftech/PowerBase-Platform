using System.Globalization;
using System.Text.RegularExpressions;
using PowerBase.Application.Formulas;
using PowerBase.Formula.Evaluation;

namespace PowerBase.Application.Imports;

/// <summary>What a formula sees of a file's cell. A file hands over every cell as text, and a date or time is written however the person typed it (7/30/2026, 07-30-2026,
/// 2026-07-30, 4:00:00 pm, 16:00), so a formula that joins them (<c>[Start Date] &amp; "/" &amp; [Start Time]</c>) gave a different text for every row. A cell that is
/// wholly a date, a time or both is shown to the formula the one way the app writes it: the date in the app's date format (<c>MM-DD-YYYY</c>...), the time as
/// <c>h:mm:ss AM/PM</c>. Any other text is left exactly as it is. Only what the formula reads is changed; the cell itself goes on to the field as it is.</summary>
public static class ImportFormulaText
{
    private static readonly Regex TimeOnly = new(@"^\d{1,2}:\d{2}(:\d{2})?(\.\d+)?\s*([AaPp][Mm])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string TimeFormat = "h:mm:ss tt";

    /// <summary>The text for a column the person said how its dates and times are written (the formats chosen on the mapping row that column goes to): read exactly in
    /// those formats and shown the one way the formula always sees them. A cell that does not fit its own format is left as it is (its row is reported by the
    /// mapping).</summary>
    public static string Normalize(string value, string? appFormat, ImportColumnFormat chosen)
    {
        var text = value.Trim();
        if (text.Length == 0) return value;
        if (chosen.DestTypeCode == "Time")
        {
            if (chosen.TimeFormat is null || !ImportDateFormats.TryParseTime(text, chosen.TimeFormat, out var stored, out _)) return value;
            return (DateTime.Today + TimeSpan.Parse(stored, CultureInfo.InvariantCulture)).ToString(TimeFormat, CultureInfo.InvariantCulture);
        }
        if (chosen.DateFormat is null) return value;
        var withTime = chosen.DestTypeCode == "DateTime";
        if (!ImportDateFormats.TryParseDate(text, chosen.DateFormat, chosen.TimeFormat, withTime, out var parsed, out _)) return value;
        var date = parsed.ToString(DotNetFormat(appFormat), CultureInfo.InvariantCulture);
        return !withTime || parsed.TimeOfDay == TimeSpan.Zero ? date : date + " " + parsed.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>The text for the formula: the date/time normalised, or <paramref name="value"/> unchanged when it is not wholly a date or time.
    /// For a column nothing said the format of: unambiguous forms are normalised, an ambiguous order follows the app's date format.</summary>
    public static string Normalize(string value, string? appFormat)
    {
        var text = value.Trim();
        if (text.Length < 4 || text.Length > 40) return value;

        if (TimeOnly.IsMatch(text))
            return DateTime.TryParse("2000-01-01 " + text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time.ToString(TimeFormat, CultureInfo.InvariantCulture) : value;

        if (!ImportDateParser.TryParse(text, appFormat, withTime: true, out var parsed)) return value;
        // A spreadsheet's time-only cell is counted from 1899-12-30: it is a time, not a date.
        if (parsed.Year <= 1900) return parsed.ToString(TimeFormat, CultureInfo.InvariantCulture);
        var date = parsed.ToString(DotNetFormat(appFormat), CultureInfo.InvariantCulture);
        return parsed.TimeOfDay == TimeSpan.Zero ? date : date + " " + parsed.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>The app's date format ("MM-DD-YYYY", "DD/MM/YY") as a .NET format ("MM-dd-yyyy"); month first when it is not known.</summary>
    private static string DotNetFormat(string? appFormat)
    {
        if (string.IsNullOrWhiteSpace(appFormat)) return "MM-dd-yyyy";
        var format = appFormat.Replace("YYYY", "yyyy", StringComparison.OrdinalIgnoreCase).Replace("YY", "yy", StringComparison.OrdinalIgnoreCase)
            .Replace("DD", "dd", StringComparison.OrdinalIgnoreCase);
        return format.Contains("yy") && format.Contains("dd") && format.Contains("MM") ? format : "MM-dd-yyyy";
    }
}

/// <summary>How the dates and times of a source column are written, as chosen on the mapping row it goes to, and the kind of field it goes to.</summary>
public sealed record ImportColumnFormat(string DestTypeCode, string? DateFormat, string? TimeFormat);

/// <summary>The row a formula reads in an import of a file: the cells as <see cref="ImportFormulaText"/> shows them. A column a mapping reads as a date or a time is
/// read in the format chosen for it, so the formula sees exactly what the field gets; any other cell is normalised by its own form.</summary>
public sealed class ImportFileFormulaContext(
    IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<long, string> fidToColumn, string? appFormat,
    IReadOnlyDictionary<int, ImportColumnFormat>? columnFormats = null) : IRecordContext
{
    private readonly RowRecordContext _inner = new(row, fidToColumn);

    public object? GetValue(long fid)
    {
        if (_inner.GetValue(fid) is not string text) return _inner.GetValue(fid);
        return columnFormats is not null && columnFormats.TryGetValue((int)fid, out var chosen)
            ? ImportFormulaText.Normalize(text, appFormat, chosen)
            : ImportFormulaText.Normalize(text, appFormat);
    }
}
