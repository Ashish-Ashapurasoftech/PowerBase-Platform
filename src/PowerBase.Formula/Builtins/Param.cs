using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

/// <summary>A parameter matcher: a human description plus a predicate over the argument's type.</summary>
internal sealed record Param(string Desc, Func<FormulaType, bool> Accepts);

/// <summary>The common parameter matchers used when declaring built-in functions.</summary>
internal static class P
{
    public static readonly Param Number = new("Number", t => t == FormulaType.Number);
    public static readonly Param Text = new("Text", t => t == FormulaType.Text);
    public static readonly Param Bool = new("Bool", t => t == FormulaType.Bool);
    public static readonly Param Date = new("Date", t => t == FormulaType.Date);
    public static readonly Param Duration = new("Duration", t => t == FormulaType.Duration);
    public static readonly Param User = new("User", t => t == FormulaType.User);
    public static readonly Param DateLike = new("Date/DateTime", t => t is FormulaType.Date or FormulaType.DateTime);
    public static readonly Param Any = new("Any", _ => true);
}
