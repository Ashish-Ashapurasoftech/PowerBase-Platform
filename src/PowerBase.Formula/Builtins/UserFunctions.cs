using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

internal static class UserFunctions
{
    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.Nullary("User", FormulaType.User,
            opt => opt.CurrentUser is null ? FormulaValue.Null(FormulaType.User) : FormulaValue.User(opt.CurrentUser)));
        r.Add(Fn.Exact("ToUser", FormulaType.User, new[] { P.Text },
            (a, _) => a[0].IsNull ? FormulaValue.Null(FormulaType.User) : FormulaValue.User(new UserRef(a[0].AsText()))));
        r.Add(Fn.Exact("UserToEmail", FormulaType.Text, new[] { P.User },
            (a, _) => FormulaValue.Text(a[0].IsNull ? string.Empty : a[0].AsUser().Email ?? string.Empty)));
        r.Add(Fn.Exact("UserToID", FormulaType.Text, new[] { P.User },
            (a, _) => FormulaValue.Text(a[0].IsNull ? string.Empty : a[0].AsUser().UserId)));
        r.Add(Fn.Exact("UserID", FormulaType.Text, new[] { P.User },
            (a, _) => FormulaValue.Text(a[0].IsNull ? string.Empty : a[0].AsUser().UserId)));
    }
}
