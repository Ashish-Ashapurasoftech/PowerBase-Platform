using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

/// <summary>
/// Platform/runtime functions that read ambient identifiers from
/// <see cref="Evaluation.EvaluationOptions"/> rather than the record: the app id
/// (<c>AppID</c>), table id (<c>Dbid</c>), frontend base URL (<c>URLRoot</c>), and the
/// page that triggered evaluation (<c>Rurl</c>). Each returns empty text when the host
/// did not supply the value.
/// </summary>
internal static class SystemFunctions
{
    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.Nullary("AppID", FormulaType.Text, opt => FormulaValue.Text(opt.AppId)));

        // Dbid() → current table's id. Dbid("Table Name") → another table's id, resolved
        // by the host's IRecordContext (empty text when the table can't be found).
        r.Add(Fn.RangeCtx("Dbid", FormulaType.Text, System.Array.Empty<Param>(), new[] { P.Text },
            (a, opt, ctx) => FormulaValue.Text(a.Count == 0 ? opt.TableId : ctx.ResolveTableId(a[0].AsText()))));

        r.Add(Fn.Nullary("URLRoot", FormulaType.Text, opt => FormulaValue.Text(opt.UrlRoot)));
        r.Add(Fn.Nullary("Rurl", FormulaType.Text, opt => FormulaValue.Text(opt.ReturnUrl)));
    }
}
