using PowerBase.Formula.Binding;

namespace PowerBase.Application.Apps.Commands;

/// <summary>
/// What a variable name may be, now that formulas read variables by writing the name in brackets
/// (<c>[Tax]</c>): it can't contain the characters that end or escape a bracketed name, and it can't look
/// like one of the id spellings (<c>var_5</c>, <c>fid_14</c>) a saved formula uses.
/// </summary>
public static class AppVariableNameRules
{
    public const string BracketMessage = "A variable name can't contain [ ] or \\ — formulas write it in square brackets, like [Tax].";

    private static readonly char[] BracketCharacters = ['[', ']', '\\'];

    public static bool HasNoBracketCharacters(string? name) => name is null || name.IndexOfAny(BracketCharacters) < 0;

    public static bool IsReserved(string? name) => VariableIdReference.IsReservedName(name) || FieldIdReference.IsReservedLabel(name);

    public static string ReservedMessage(string? name) =>
        $"The name '{name?.Trim()}' is reserved for referencing by ID in formulas (names like 'var_5' or 'fid_14'). Choose a different name.";
}
