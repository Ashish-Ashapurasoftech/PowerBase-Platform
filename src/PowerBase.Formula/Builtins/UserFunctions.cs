using System.Text.RegularExpressions;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

internal static class UserFunctions
{
    // Pragmatic email shape check: non-space local + domain with a dot. Good enough for
    // a formula predicate; not a full RFC 5322 validator.
    private static readonly Regex EmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.Nullary("User", FormulaType.User,
            opt => opt.CurrentUser is null ? FormulaValue.Null(FormulaType.User) : FormulaValue.User(opt.CurrentUser)));
        r.Add(Fn.Exact("ToUser", FormulaType.User, new[] { P.Text },
            (a, _) => a[0].IsNull ? FormulaValue.Null(FormulaType.User) : FormulaValue.User(new UserRef(a[0].AsText()))));
        r.Add(Fn.Exact("UserToEmail", FormulaType.Text, new[] { P.User },
            (a, _) => FormulaValue.Text(a[0].IsNull ? string.Empty : a[0].AsUser().Email ?? string.Empty)));
        r.Add(Fn.Exact("UserToName", FormulaType.Text, new[] { P.User },
            (a, _) => FormulaValue.Text(a[0].IsNull ? string.Empty : a[0].AsUser().Name ?? string.Empty)));
        r.Add(Fn.Exact("UserToID", FormulaType.Text, new[] { P.User },
            (a, _) => FormulaValue.Text(a[0].IsNull ? string.Empty : a[0].AsUser().UserId)));
        r.Add(Fn.Exact("UserID", FormulaType.Text, new[] { P.User },
            (a, _) => FormulaValue.Text(a[0].IsNull ? string.Empty : a[0].AsUser().UserId)));
        r.Add(Fn.Exact("IsUserEmail", FormulaType.Bool, new[] { P.Text },
            (a, _) => FormulaValue.Bool(IsEmail(a[0].AsText()))));
    }

    private static bool IsEmail(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        try { return EmailRegex.IsMatch(s); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
