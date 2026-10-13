using System.Globalization;
using PowerBase.Formula.Syntax;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Binding;

/// <summary>
/// <c>GetFieldValue(14)</c> / <c>GetFieldValue("fid_14")</c>: the value of a field named by its ID.
///
/// When the ID is known before the formula runs — written in the call, or held by a variable that
/// is set to a plain number or text (<c>var number cfg = 14; GetFieldValue($cfg)</c>) — the binder
/// resolves it exactly like <c>[fid_14]</c>: same type, same dependency, so the type checker reports
/// "Formula returns Date but Number is required" and the like.
///
/// When it is only known per record (<c>GetFieldValue([Config Field])</c>) the call is marked
/// dynamic: it type-checks as "unknown" (accepted anywhere) and is resolved, and its type verified,
/// when the formula runs — see the Evaluator.
///
/// Handled by the binder, type checker and evaluator directly, not through the function registry,
/// because its result type depends on the field it names.
/// </summary>
internal static class FieldValueCall
{
    public const string Name = "GetFieldValue";

    public static bool IsCall(FunctionCallExpr call) => string.Equals(call.Name, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The literal an argument stands for: itself if it is one, or the literal a constant
    /// variable was set to. False for anything only known when the formula runs.</summary>
    public static bool TryGetLiteral(Expr arg, IReadOnlyDictionary<string, LiteralExpr> constants, out LiteralExpr literal)
    {
        switch (arg)
        {
            case LiteralExpr l:
                literal = l;
                return true;
            case VariableRefExpr v when constants.TryGetValue(v.Name, out var constant):
                literal = constant;
                return true;
            default:
                literal = null!;
                return false;
        }
    }

    /// <summary>The field number a value spells out: <c>14</c>, <c>"14"</c> or <c>"fid_14"</c>.</summary>
    public static bool TryGetFieldNumber(FormulaValue value, out long fid)
    {
        fid = 0;
        if (value.IsNull) return false;

        switch (value.Type)
        {
            case FormulaType.Number:
                var number = value.AsNumber();
                if (number < 0 || number != decimal.Truncate(number) || number > long.MaxValue) return false;
                fid = (long)number;
                return true;
            case FormulaType.Text:
                var text = value.AsText().Trim();
                return FieldIdReference.TryParse(text, out fid)
                    || long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out fid);
            default:
                return false;
        }
    }
}
