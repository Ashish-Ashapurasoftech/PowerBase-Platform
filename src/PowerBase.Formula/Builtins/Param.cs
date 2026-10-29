using PowerBase.Formula.Types;

namespace PowerBase.Formula.Builtins;

/// <summary>A parameter matcher: a human description plus a predicate over the argument's type.</summary>
internal sealed record Param(string Desc, Func<FormulaType, bool> Accepts);

/// <summary>The common parameter matchers used when declaring built-in functions.</summary>
internal static class P
{
    public static readonly Param Number = new("Number", t => t == FormulaType.Number);
    public static readonly Param NumberOrText = new("Number or Text", t => t is FormulaType.Number or FormulaType.Text);
    public static readonly Param Text = new("Text", t => t == FormulaType.Text);
    public static readonly Param Bool = new("Bool", t => t == FormulaType.Bool);
    public static readonly Param Date = new("Date", t => t == FormulaType.Date);
    public static readonly Param Duration = new("Duration", t => t == FormulaType.Duration);
    public static readonly Param User = new("User", t => t == FormulaType.User);
    public static readonly Param Time = new("Time", t => t == FormulaType.Time);
    public static readonly Param DateLike = new("Date/DateTime", t => t is FormulaType.Date or FormulaType.DateTime);
    /// <summary>Hour / Minute / Second read the time of day of a timestamp, of a time, or (as midnight) of a date.</summary>
    public static readonly Param TimeLike = new("Date/DateTime/Time", t => t is FormulaType.Date or FormulaType.DateTime or FormulaType.Time);
    public static readonly Param TextList = new("Text List", t => t == FormulaType.TextList);
    public static readonly Param RecordList = new("Record List", t => t == FormulaType.RecordList);
    public static readonly Param List = new("List", t => t is FormulaType.TextList or FormulaType.UserList or FormulaType.RecordList);
    public static readonly Param Any = new("Any", _ => true);
}
