using System.Globalization;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas;

/// <summary>Converts an engine <see cref="FormulaValue"/> to a JSON-friendly raw value for DTOs and records.</summary>
internal static class FormulaRawValue
{
    public static object? ToRaw(FormulaValue v)
    {
        if (v.IsNull) return null;
        return v.Type switch
        {
            FormulaType.Text => v.AsText(),
            FormulaType.Number => v.AsNumber(),
            FormulaType.Bool => v.AsBool(),
            FormulaType.Date => v.AsDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FormulaType.DateTime => v.AsDateTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            FormulaType.Duration => (decimal)v.AsDuration().TotalMinutes,
            FormulaType.User => v.AsUser().UserId,
            FormulaType.UserList => v.AsUserList().Select(u => u.UserId).ToList(),
            FormulaType.TextList => v.AsTextList().ToList(),
            FormulaType.RecordList => v.AsRecordList().RecordIds.ToList(),
            _ => null,
        };
    }
}
