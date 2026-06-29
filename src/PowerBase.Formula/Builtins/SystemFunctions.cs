using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

/// <summary>
/// Platform/runtime functions that read ambient identifiers from
/// <see cref="Evaluation.EvaluationOptions"/> rather than the record: the app id
/// (<c>AppID</c>), table id (<c>Dbid</c>), and frontend base URL (<c>URLRoot</c>).
/// Each returns empty text when the host did not supply the value.
/// </summary>
internal static class SystemFunctions
{
    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.Nullary("AppID", FormulaType.Text, opt => FormulaValue.Text(opt.AppId)));
        r.Add(Fn.Nullary("Dbid", FormulaType.Text, opt => FormulaValue.Text(opt.TableId)));
        r.Add(Fn.Nullary("URLRoot", FormulaType.Text, opt => FormulaValue.Text(opt.UrlRoot)));
    }
}
