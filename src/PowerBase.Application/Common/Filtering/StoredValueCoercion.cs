using System.Globalization;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Filtering;

/// <summary>
/// In-memory filters and sorts compare a stored value with an operand. An encrypted field is decrypted to text, whatever its type, so
/// "9000" &gt; "10000" compared as text — a Number filter "&gt; 10000" matched almost every record. This turns such text back into the
/// field's own type (number, date, checkbox) before comparing. Values that already have a type, and text that does not parse, are
/// returned unchanged.
/// </summary>
public static class StoredValueCoercion
{
    public static object? ToFieldType(object? value, AppField? field)
    {
        if (value is not string s || field == null) return value;
        var text = s.Trim();
        if (text.Length == 0) return value;
        // Ranges are stored as JSON and compared by their own operators.
        if (field.TypeCode is "NumericRange" or "DateRange") return value;

        switch (PipelineFilterEvaluator.GetTypeCategory(field))
        {
            case "NUMBER":
                const NumberStyles numberStyles = NumberStyles.Number | NumberStyles.AllowExponent;
                if (decimal.TryParse(text, numberStyles, CultureInfo.InvariantCulture, out var number) ||
                    decimal.TryParse(text, numberStyles, CultureInfo.CurrentCulture, out number))
                    return number;
                return value;
            case "DATE":
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
                    return date;
                return value;
            case "TIME":
                return PipelineFilterEvaluator.TryParseTimeOfDay(text, out var time) ? time : value;
            case "BOOLEAN":
                if (bool.TryParse(text, out var flag)) return flag;
                if (text == "1") return true;
                if (text == "0") return false;
                return value;
            default:
                return value;
        }
    }
}
