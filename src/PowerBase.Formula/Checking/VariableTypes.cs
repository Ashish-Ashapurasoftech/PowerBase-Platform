using PowerBase.Formula.Types;

namespace PowerBase.Formula.Checking;

/// <summary>
/// The type keywords a <c>var &lt;type&gt; &lt;name&gt; = …;</c> declaration may use, and their
/// <see cref="FormulaType"/>. Quickbase's keyword set; <c>list-user</c> carries a hyphen, which the
/// parser stitches back together from its tokens.
/// </summary>
public static class VariableTypes
{
    private static readonly Dictionary<string, FormulaType> ByKeyword = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = FormulaType.Text,
        ["number"] = FormulaType.Number,
        ["bool"] = FormulaType.Bool,
        ["date"] = FormulaType.Date,
        ["timestamp"] = FormulaType.DateTime,
        ["timeofday"] = FormulaType.Time,
        ["duration"] = FormulaType.Duration,
        ["user"] = FormulaType.User,
        ["list-user"] = FormulaType.UserList,
    };

    public static bool TryParse(string keyword, out FormulaType type) => ByKeyword.TryGetValue(keyword, out type);

    /// <summary>The keyword an author would write for <paramref name="type"/> — used in messages
    /// such as "Expecting number but found text".</summary>
    public static string Keyword(FormulaType type) => type switch
    {
        FormulaType.Text => "text",
        FormulaType.Number => "number",
        FormulaType.Bool => "bool",
        FormulaType.Date => "date",
        FormulaType.DateTime => "timestamp",
        FormulaType.Time => "timeofday",
        FormulaType.Duration => "duration",
        FormulaType.User => "user",
        FormulaType.UserList => "list-user",
        FormulaType.TextList => "text list",
        FormulaType.RecordList => "record list",
        _ => type.ToString().ToLowerInvariant(),
    };

    public static string Supported => string.Join(", ", ByKeyword.Keys);
}
