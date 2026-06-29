using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Functions;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

/// <summary>
/// List functions over the <see cref="FormulaType.TextList"/> (and, where it makes
/// sense, <see cref="FormulaType.UserList"/>) types. <c>Split</c> produces a text
/// list; <c>Join</c> collapses one back to text; <c>Count</c>/<c>Size</c> measure a
/// list; <c>ToUserList</c> builds a user list from text/a list.
/// </summary>
internal static class ListFunctions
{
    public static void Register(FunctionRegistry r)
    {
        r.Add(Fn.Exact("Split", FormulaType.TextList, new[] { P.Text, P.Text },
            (a, _) => FormulaValue.TextList(Split(a[0].AsText(), a[1].AsText()))));
        r.Add(Fn.Exact("Join", FormulaType.Text, new[] { P.List, P.Text },
            (a, _) => FormulaValue.Text(string.Join(a[1].AsText(), AsStrings(a[0])))));
        r.Add(Fn.Exact("Count", FormulaType.Number, new[] { P.List },
            (a, _) => FormulaValue.Number(ListLength(a[0]))));
        r.Add(Fn.Exact("Size", FormulaType.Number, new[] { P.List },
            (a, _) => FormulaValue.Number(ListLength(a[0]))));
        r.Add(Fn.Exact("ToUserList", FormulaType.UserList, new[] { P.Any },
            (a, _) => FormulaValue.UserList(ToUserRefs(a[0]))));
    }

    // Empty separator returns the whole string as a single element (mirrors Part's empty-delim rule).
    private static IReadOnlyList<string> Split(string s, string sep)
        => sep.Length == 0 ? new[] { s } : s.Split(sep);

    private static IReadOnlyList<string> AsStrings(FormulaValue v) => v.Type switch
    {
        FormulaType.TextList => v.AsTextList(),
        FormulaType.UserList => v.IsNull ? Array.Empty<string>() : v.AsUserList().Select(u => u.Email ?? u.UserId).ToList(),
        _ => Array.Empty<string>(),
    };

    private static int ListLength(FormulaValue v) => v.Type switch
    {
        FormulaType.TextList => v.AsTextList().Count,
        FormulaType.UserList => v.IsNull ? 0 : v.AsUserList().Count,
        FormulaType.RecordList => v.AsRecordList().RecordIds.Count,
        _ => 0,
    };

    private static IReadOnlyList<UserRef> ToUserRefs(FormulaValue v)
    {
        if (v.IsNull) return Array.Empty<UserRef>();
        switch (v.Type)
        {
            case FormulaType.UserList:
                return v.AsUserList();
            case FormulaType.User:
                return new[] { v.AsUser() };
            case FormulaType.TextList:
                return v.AsTextList()
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => new UserRef(s.Trim()))
                        .ToList();
            default:
                return ValueConvert.ToTextString(v)
                        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => new UserRef(s))
                        .ToList();
        }
    }
}
